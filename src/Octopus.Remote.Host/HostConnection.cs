using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Octopus.Remote.Protocol;
using Octopus.Remote.Security;

namespace Octopus.Remote.Host;

/// <summary>One connected device: answers its requests from the workspace and feeds it the terminals it attached.</summary>
internal sealed class HostConnection : IAsyncDisposable
{
    private readonly RemoteConnection _connection;
    private readonly IHostWorkspace _workspace;
    private readonly HelloMessage _hello;
    private readonly ConcurrentDictionary<uint, Subscriber> _subscribers = new();
    private readonly ConcurrentDictionary<string, IDisposable> _watches = new(StringComparer.Ordinal);
    private volatile HelloMessage? _theirs;

    public HostConnection(RemoteConnection connection, IHostWorkspace workspace, HelloMessage hello)
    {
        _connection = connection;
        _workspace = workspace;
        _hello = hello;
    }

    public DevicePublicKey Peer => _connection.Peer;
    public DeviceDescription PeerDescription => _connection.PeerDescription;
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public Task Completion => _connection.Completion;
    /// <summary>How many terminals the device has attached now.</summary>
    public int AttachedCount => _subscribers.Count;

    public void Start()
    {
        var json = RemoteJsonContext.Default;
        RemoteHello.Answer(_connection, _hello, theirs => _theirs = theirs);
        _connection.Handle(RemoteMethods.HostInfo, json.Nothing, json.HostInfoMessage, (_, _) => Task.FromResult(_workspace.Info));
        _connection.Handle(RemoteMethods.ProjectList, json.Nothing, json.ProjectListMessage,
            async (_, _) => new ProjectListMessage { Projects = [.. await _workspace.ListProjectsAsync().ConfigureAwait(false)] });
        _connection.Handle(RemoteMethods.ProjectCreate, json.CreateProjectMessage, json.ProjectMessage, (request, _) => _workspace.CreateProjectAsync(request));
        _connection.Handle(RemoteMethods.ItemList, json.ProjectRefMessage, json.ItemListMessage,
            async (request, _) => new ItemListMessage { Items = [.. await _workspace.ListItemsAsync(request.ProjectId).ConfigureAwait(false)] });
        _connection.Handle(RemoteMethods.ItemCreate, json.CreateItemMessage, json.ItemMessage, (request, _) => _workspace.CreateItemAsync(request));
        _connection.Handle(RemoteMethods.ItemRemove, json.ItemRefMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.RemoveItemAsync(request.ItemId).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.ItemRename, json.RenameItemMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.RenameItemAsync(request.ItemId, request.Title).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.WorkspaceOptions, json.ProjectRefMessage, json.WorkspaceOptionsMessage,
            (request, _) => _workspace.GetWorkspaceOptionsAsync(request.ProjectId));
        _connection.Handle(RemoteMethods.WorkspaceCreate, json.CreateWorkspaceMessage, json.WorkspaceMessage,
            (request, _) => _workspace.CreateWorkspaceAsync(request));
        _connection.Handle(RemoteMethods.AgentCatalog, json.Nothing, json.AgentListMessage,
            async (_, _) => new AgentListMessage { Agents = [.. await _workspace.ListAgentsAsync().ConfigureAwait(false)] });
        _connection.Handle(RemoteMethods.SessionAttach, json.AttachMessage, json.AttachedMessage, (request, _) => AttachAsync(request));
        _connection.Handle(RemoteMethods.SessionDetach, json.ChannelMessage, json.Nothing, (request, _) =>
        {
            if (_subscribers.TryRemove(request.Channel, out var subscriber)) subscriber.Dispose();
            return Task.FromResult(new Nothing());
        });
        _connection.Handle(RemoteMethods.SessionResize, json.ResizeMessage, json.ResizedMessage, (request, _) =>
        {
            if (!_subscribers.TryGetValue(request.Channel, out var subscriber)) throw new RemoteException(RemoteMethods.NotFound, "No terminal is attached on this channel.");
            var (applied, columns, rows) = subscriber.Attachment!.Resize(request.Columns, request.Rows, request.Claim);
            return Task.FromResult(new ResizedMessage { Applied = applied, Columns = columns, Rows = rows });
        });
        _connection.Handle(RemoteMethods.SessionRelease, json.ChannelMessage, json.ResizedMessage, (request, _) =>
        {
            if (!_subscribers.TryGetValue(request.Channel, out var subscriber)) throw new RemoteException(RemoteMethods.NotFound, "No terminal is attached on this channel.");
            var (columns, rows) = subscriber.Attachment!.ReleaseSize();
            return Task.FromResult(new ResizedMessage { Applied = false, Columns = columns, Rows = rows });
        });
        _connection.Handle(RemoteMethods.SessionRestart, json.ItemRefMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.RestartAsync(request.ItemId).ConfigureAwait(false);
            return new Nothing();
        });
        RegisterFiles(json);
        _connection.DataReceived = OnData;
        _workspace.Changed += OnWorkspaceChanged;
        _workspace.ItemChanged += OnItemChanged;
        _connection.Start();
        _ = _connection.Completion.ContinueWith(_ => Release(), TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        Release();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private void Release()
    {
        _workspace.Changed -= OnWorkspaceChanged;
        _workspace.ItemChanged -= OnItemChanged;
        foreach (var channel in _subscribers.Keys)
        {
            if (_subscribers.TryRemove(channel, out var subscriber)) subscriber.Dispose();
        }
        foreach (var project in _watches.Keys)
        {
            if (_watches.TryRemove(project, out var watch)) watch.Dispose();
        }
    }

    private static void Forget(Task task) => _ = task;

    private void RegisterFiles(RemoteJsonContext json)
    {
        _connection.Handle(RemoteMethods.FsResolve, json.PathMessage, json.ResolvedMessage,
            async (request, _) => new ResolvedMessage { Path = await _workspace.ResolveAsync(request.Path).ConfigureAwait(false) });
        _connection.Handle(RemoteMethods.FsList, json.PathMessage, json.FileListMessage,
            async (request, _) => new FileListMessage { Entries = [.. await _workspace.ListAsync(request.Path).ConfigureAwait(false)] });
        _connection.Handle(RemoteMethods.FsExists, json.PathMessage, json.ExistsMessage,
            async (request, _) => new ExistsMessage { Exists = await _workspace.ExistsAsync(request.Path).ConfigureAwait(false) });
        _connection.Handle(RemoteMethods.FsRead, json.ReadFileMessage, json.FilePieceMessage, async (request, _) =>
        {
            if (request.Offset < 0 || request.Length is < 0 or > RemoteMethods.FilePieceBytes) throw new RemoteException(RemoteProtocol.InvalidParams, "A piece is too large.");
            var (data, size) = await _workspace.ReadAsync(request.Path, request.Offset, request.Length).ConfigureAwait(false);
            return new FilePieceMessage { Size = size, Data = Convert.ToBase64String(data) };
        });
        _connection.Handle(RemoteMethods.FsWrite, json.WriteFileMessage, json.Nothing, async (request, _) =>
        {
            byte[] data;
            try { data = Convert.FromBase64String(request.Data); }
            catch (FormatException) { throw new RemoteException(RemoteProtocol.InvalidParams, "The data is not base64."); }
            if (data.Length > RemoteMethods.FilePieceBytes || request.Upload.Length is 0 or > 64) throw new RemoteException(RemoteProtocol.InvalidParams, "A piece is too large.");
            await _workspace.WriteAsync(request.Path, request.Upload, data, request.Last).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.FsCreateFile, json.PathMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.CreateFileAsync(request.Path).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.FsCreateDirectory, json.PathMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.CreateDirectoryAsync(request.Path).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.FsRename, json.MoveMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.RenameAsync(request.Path, request.NewPath).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.FsDelete, json.DeleteMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.DeleteAsync(request.Path, request.IsDirectory).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.FsWatch, json.PathMessage, json.Nothing, async (request, cancellation) =>
        {
            var folder = request.Path;
            if (!_watches.ContainsKey(folder))
            {
                var watch = await _workspace.WatchAsync(folder, () => Forget(NotifyAsync(RemoteMethods.FsChanged, new PathMessage { Path = folder }, RemoteJsonContext.Default.PathMessage))).ConfigureAwait(false);
                if (!_watches.TryAdd(folder, watch)) watch.Dispose();
            }
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.FsUnwatch, json.PathMessage, json.Nothing, (request, _) =>
        {
            if (_watches.TryRemove(request.Path, out var watch)) watch.Dispose();
            return Task.FromResult(new Nothing());
        });
        _connection.Handle(RemoteMethods.FsBrowse, json.BrowseFolderMessage, json.BrowseFolderResultMessage,
            (request, _) => _workspace.BrowseFolderAsync(request));
        _connection.Handle(RemoteMethods.FsBrowseCreateDirectory, json.CreateBrowseFolderMessage, json.ResolvedMessage,
            async (request, _) => new ResolvedMessage { Path = await _workspace.BrowseCreateDirectoryAsync(request).ConfigureAwait(false) });
        _connection.Handle(RemoteMethods.GitCall, json.CallMessage, json.CallResultMessage,
            async (request, _) => new CallResultMessage { Result = await _workspace.GitAsync(request.Method, request.Arguments).ConfigureAwait(false) });
    }

    private async Task<AttachedMessage> AttachAsync(AttachMessage request)
    {
        if (request.Channel == 0 || request.Columns < 2 || request.Rows < 1) throw new RemoteException(RemoteProtocol.InvalidParams, "A channel and a size are needed.");
        var subscriber = new Subscriber(this, request.Channel);
        if (!_subscribers.TryAdd(request.Channel, subscriber)) throw new RemoteException(RemoteProtocol.InvalidParams, "The channel is in use.");
        try
        {
            // The library's own snapshot only between two builds of the same terminal library (design/12 §4.3).
            var native = request.Snapshot == SnapshotFormats.Native && _theirs?.TerminalBuild is { Length: > 0 } theirs && theirs == _hello.TerminalBuild;
            var attachment = await _workspace.AttachAsync(request.ItemId, request.Columns, request.Rows, native, request.ClaimSize, subscriber).ConfigureAwait(false);
            var snapshotLength = attachment.SnapshotLength;
            subscriber.Begin(attachment);
            return new AttachedMessage
            {
                Channel = request.Channel,
                Position = attachment.Position,
                Snapshot = attachment.IsNative ? SnapshotFormats.Native : SnapshotFormats.Vt,
                SnapshotLength = snapshotLength,
                Columns = attachment.Columns,
                Rows = attachment.Rows,
                Title = attachment.Title,
                IsRunning = attachment.IsRunning,
                ExitCode = attachment.ExitCode,
                OwnsSize = attachment.OwnsSize,
            };
        }
        catch
        {
            _subscribers.TryRemove(request.Channel, out _);
            subscriber.Dispose();
            throw;
        }
    }

    private void OnData(FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        if (!_subscribers.TryGetValue(header.Channel, out var subscriber)) return;
        if (header.Type == FrameType.Input) subscriber.Attachment?.Input(payload.Span);
        else if (header.Type == FrameType.Ack && payload.Length >= 8) subscriber.Acknowledge(BinaryPrimitives.ReadUInt64LittleEndian(payload.Span));
    }

    private void OnWorkspaceChanged() => _ = NotifyAsync(RemoteMethods.WorkspaceChanged, new Nothing(), RemoteJsonContext.Default.Nothing);

    private void OnItemChanged(ItemMessage item) => _ = NotifyAsync(RemoteMethods.ItemChanged, item, RemoteJsonContext.Default.ItemMessage);

    private async Task NotifyAsync<T>(string method, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try { await _connection.NotifyAsync(method, value, type).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    /// <summary>
    /// What one attached terminal sends to the device, in order: its snapshot, then output and records as they come.
    /// The terminal's reader only queues here and never waits for the network. A device that does not keep up is cut
    /// off rather than slowing the program down: past <see cref="MaxBacklog"/> unacknowledged bytes it is told to
    /// attach again later for a fresh snapshot (design/12 §6.4).
    /// </summary>
    private sealed class Subscriber(HostConnection owner, uint channel) : ISessionSink, IDisposable
    {
        public const int MaxBacklog = 1 << 20;
        private const int MaxFrame = 60 * 1024;
        private const int SnapshotPiece = 256 * 1024;

        private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
        private readonly object _gate = new();
        private ulong _acknowledged;
        private bool _lagging;
        private int _disposed;

        private readonly record struct Item(byte Kind, ulong Position, byte[] Bytes, int Number = 0, int Other = 0);
        private const byte KindOutput = 0, KindResize = 1, KindReset = 2, KindExit = 3;

        public ISessionAttachment? Attachment { get; private set; }

        /// <summary>The attachment exists: the snapshot goes out first, then what was queued since.</summary>
        public void Begin(ISessionAttachment attachment)
        {
            Attachment = attachment;
            lock (_gate) _acknowledged = attachment.Position;
            var snapshot = attachment.TakeSnapshot();
            _ = Task.Run(() => SendLoopAsync(snapshot));
        }

        public void Acknowledge(ulong position)
        {
            lock (_gate)
            {
                if (position > _acknowledged) _acknowledged = position;
            }
        }

        public void Output(ulong position, ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                if (_lagging) return;
                if (position + (ulong)bytes.Length - _acknowledged > MaxBacklog)
                {
                    _lagging = true;
                    _queue.Writer.TryComplete();
                    _ = owner.FallBehindAsync(channel, this);
                    return;
                }
            }
            _queue.Writer.TryWrite(new Item(KindOutput, position, bytes.ToArray()));
        }

        public void Resized(ulong position, int columns, int rows) => _queue.Writer.TryWrite(new Item(KindResize, position, [], columns, rows));

        public void Reset(ulong position) => _queue.Writer.TryWrite(new Item(KindReset, position, []));

        public void Exited(int? exitCode) => _queue.Writer.TryWrite(new Item(KindExit, 0, [], exitCode ?? 0, exitCode is null ? 0 : 1));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _queue.Writer.TryComplete();
            Attachment?.Dispose();
        }

        private async Task SendLoopAsync(byte[] snapshot)
        {
            try
            {
                var connection = owner._connection;
                for (var offset = 0; ; offset += SnapshotPiece)
                {
                    var length = Math.Min(SnapshotPiece, snapshot.Length - offset);
                    var last = offset + length >= snapshot.Length;
                    var piece = new byte[1 + length];
                    piece[0] = last ? (byte)1 : (byte)0;
                    snapshot.AsSpan(offset, length).CopyTo(piece.AsSpan(1));
                    await connection.SendDataAsync(FrameType.Snapshot, channel, piece).ConfigureAwait(false);
                    if (last) break;
                }

                var frame = new byte[8 + MaxFrame];
                await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    switch (item.Kind)
                    {
                        case KindOutput:
                            // Pieces that queued up while the network was busy go out as one frame.
                            BinaryPrimitives.WriteUInt64LittleEndian(frame, item.Position);
                            var used = 0;
                            var next = item;
                            while (true)
                            {
                                for (var at = 0; at < next.Bytes.Length;)
                                {
                                    var take = Math.Min(MaxFrame - used, next.Bytes.Length - at);
                                    next.Bytes.AsSpan(at, take).CopyTo(frame.AsSpan(8 + used));
                                    used += take;
                                    at += take;
                                    if (used == MaxFrame)
                                    {
                                        await connection.SendDataAsync(FrameType.Output, channel, frame.AsMemory(0, 8 + used)).ConfigureAwait(false);
                                        BinaryPrimitives.WriteUInt64LittleEndian(frame, next.Position + (ulong)at);
                                        used = 0;
                                    }
                                }
                                if (used == 0 || !_queue.Reader.TryPeek(out var peeked) || peeked.Kind != KindOutput) break;
                                _queue.Reader.TryRead(out next);
                            }
                            if (used > 0) await connection.SendDataAsync(FrameType.Output, channel, frame.AsMemory(0, 8 + used)).ConfigureAwait(false);
                            break;
                        case KindResize:
                            var resize = new byte[13];
                            BinaryPrimitives.WriteUInt64LittleEndian(resize, item.Position);
                            resize[8] = RecordKinds.Resize;
                            BinaryPrimitives.WriteUInt16LittleEndian(resize.AsSpan(9), (ushort)Math.Clamp(item.Number, 0, ushort.MaxValue));
                            BinaryPrimitives.WriteUInt16LittleEndian(resize.AsSpan(11), (ushort)Math.Clamp(item.Other, 0, ushort.MaxValue));
                            await connection.SendDataAsync(FrameType.Record, channel, resize).ConfigureAwait(false);
                            break;
                        case KindReset:
                            var reset = new byte[9];
                            BinaryPrimitives.WriteUInt64LittleEndian(reset, item.Position);
                            reset[8] = RecordKinds.Reset;
                            await connection.SendDataAsync(FrameType.Record, channel, reset).ConfigureAwait(false);
                            break;
                        case KindExit:
                            await connection.NotifyAsync(RemoteMethods.SessionExited,
                                new ExitedMessage { Channel = channel, ExitCode = item.Other == 0 ? null : item.Number },
                                RemoteJsonContext.Default.ExitedMessage).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The connection ended; its completion releases everything.
            }
        }
    }

    // The device does not keep up with this terminal: stop feeding it and say so. It attaches again when it is ready.
    private async Task FallBehindAsync(uint channel, Subscriber subscriber)
    {
        // Not on the terminal's own thread: letting go of the attachment changes the list that thread is walking.
        await Task.Yield();
        if (_subscribers.TryRemove(channel, out _)) subscriber.Dispose();
        await NotifyAsync(RemoteMethods.SessionResync, new ChannelMessage { Channel = channel }, RemoteJsonContext.Default.ChannelMessage).ConfigureAwait(false);
    }
}
