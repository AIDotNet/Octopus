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
    private readonly ConcurrentDictionary<uint, StreamSendWindow> _streamSenders = new();
    private readonly ConcurrentDictionary<uint, StreamWriteReceiver> _streamReceivers = new();
    private readonly ConcurrentDictionary<string, IDisposable> _watches = new(StringComparer.Ordinal);
    // The GUI conversations the device looks at, by item id; guarded by itself.
    private readonly Dictionary<string, ConversationSubscriber> _conversations = new(StringComparer.Ordinal);
    // Highest sequence sent on this connection for each item, so re-attaching never rewinds past an in-flight update.
    private readonly Dictionary<string, long> _conversationSequences = new(StringComparer.Ordinal);
    private readonly Func<HostConnection, P2pHandshakeMessage, CancellationToken, Task<P2pHandshakeMessage>>? _onP2pHandshake;
    private volatile HelloMessage? _theirs;

    public HostConnection(
        RemoteConnection connection,
        IHostWorkspace workspace,
        HelloMessage hello,
        Func<HostConnection, P2pHandshakeMessage, CancellationToken, Task<P2pHandshakeMessage>>? onP2pHandshake = null)
    {
        _connection = connection;
        _workspace = workspace;
        _hello = hello;
        _onP2pHandshake = onP2pHandshake;
    }

    public RemoteConnection Connection => _connection;
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
        _connection.Handle(RemoteMethods.ProjectIcon, json.ProjectRefMessage, json.ProjectIconMessage,
            async (request, _) => await _workspace.GetProjectIconAsync(request.ProjectId).ConfigureAwait(false) ?? new ProjectIconMessage());
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
        RegisterConversations(json);
        RegisterBots(json);
        _connection.Handle(RemoteMethods.SystemMetrics, json.SystemMetricsRequestMessage, json.SystemMetricsSnapshotMessage,
            (request, ct) => _workspace.GetSystemMetricsAsync(request, ct));
        _connection.Handle(RemoteMethods.SystemSignalProcess, json.SignalProcessMessage, json.SignalProcessResultMessage,
            async (request, ct) => new SignalProcessResultMessage { Ok = await _workspace.SignalProcessAsync(request, ct).ConfigureAwait(false) });
        _connection.Handle(RemoteMethods.ProcessRun, json.RemoteProcessRunMessage, json.RemoteProcessRunResultMessage,
            (request, ct) => _workspace.RunProcessAsync(request, ct));
        if (_onP2pHandshake is { } p2p)
        {
            _connection.Handle(RemoteMethods.TransportP2pHandshake, json.P2pHandshakeMessage, json.P2pHandshakeMessage,
                (request, ct) => p2p(this, request, ct));
        }
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
        foreach (var channel in _streamSenders.Keys)
        {
            if (_streamSenders.TryRemove(channel, out var sender)) sender.Abort();
        }
        foreach (var channel in _streamReceivers.Keys)
        {
            if (_streamReceivers.TryRemove(channel, out var receiver)) receiver.Abort();
        }
        foreach (var project in _watches.Keys)
        {
            if (_watches.TryRemove(project, out var watch)) watch.Dispose();
        }
        List<ConversationSubscriber> conversations;
        lock (_conversations)
        {
            conversations = [.. _conversations.Values];
            _conversations.Clear();
        }
        foreach (var conversation in conversations) conversation.Dispose();
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
        _connection.Handle(RemoteMethods.FsUpload, json.UploadMessage, json.UploadedMessage, async (request, _) =>
        {
            byte[] data;
            try { data = Convert.FromBase64String(request.Data); }
            catch (FormatException) { throw new RemoteException(RemoteProtocol.InvalidParams, "The data is not base64."); }
            if (data.Length > RemoteMethods.FilePieceBytes || request.Upload.Length is 0 or > 64) throw new RemoteException(RemoteProtocol.InvalidParams, "A piece is too large.");
            return new UploadedMessage { Path = await _workspace.UploadAsync(request.ItemId, request.Upload, request.Name, data, request.Last).ConfigureAwait(false) };
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
        _connection.Handle(RemoteMethods.FsReadStream, json.ReadStreamMessage, json.ReadStreamReadyMessage, (request, _) => StartReadStreamAsync(request));
        _connection.Handle(RemoteMethods.FsWriteStream, json.WriteStreamMessage, json.Nothing, (request, _) => StartWriteStreamAsync(request));
        _connection.Handle(RemoteMethods.FsWriteFinish, json.ChannelMessage, json.Nothing, (request, _) => FinishWriteStreamAsync(request.Channel));
        _connection.Handle(RemoteMethods.FsUploadStream, json.UploadStreamMessage, json.Nothing, (request, _) => StartUploadStreamAsync(request));
        _connection.Handle(RemoteMethods.FsUploadFinish, json.ChannelMessage, json.UploadedMessage, (request, _) => FinishUploadStreamAsync(request.Channel));
    }

    private void RegisterConversations(RemoteJsonContext json)
    {
        _connection.Handle(RemoteMethods.AcpAttach, json.AcpRefMessage, json.AcpSnapshotMessage, (request, _) => AttachConversationAsync(request.ItemId));
        _connection.Handle(RemoteMethods.AcpDetach, json.AcpRefMessage, json.Nothing, (request, _) =>
        {
            StopConversation(request.ItemId);
            return Task.FromResult(new Nothing());
        });
        _connection.Handle(RemoteMethods.AcpPrompt, json.AcpPromptMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.PromptAsync(request).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.AcpCancel, json.AcpRefMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.CancelTurnAsync(request.ItemId).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.AcpRespond, json.AcpRespondMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.RespondAsync(request).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.AcpSetOption, json.AcpSetOptionMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.SetConversationOptionAsync(request).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.AcpMentionTargets, json.AcpMentionTargetsRequestMessage, json.AcpMentionTargetsMessage,
            (request, _) => _workspace.ListMentionTargetsAsync(request));
    }

    private void RegisterBots(RemoteJsonContext json)
    {
        _connection.Handle(RemoteMethods.BotList, json.Nothing, json.BotListMessage,
            async (_, _) => new BotListMessage { Bots = [.. await _workspace.ListBotsAsync().ConfigureAwait(false)] });
        _connection.Handle(RemoteMethods.BotCreate, json.CreateBotMessage, json.BotMessage,
            (request, _) => _workspace.CreateBotAsync(request));
        _connection.Handle(RemoteMethods.BotImport, json.ImportBotMessage, json.BotMessage,
            (request, _) => _workspace.ImportBotAsync(request));
        _connection.Handle(RemoteMethods.BotUpdate, json.UpdateBotMessage, json.BotMessage,
            (request, _) => _workspace.UpdateBotAsync(request));
        _connection.Handle(RemoteMethods.BotDelete, json.BotRefMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.DeleteBotAsync(request.BotId).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.BotChatCreate, json.CreateBotChatMessage, json.BotChatMessage,
            (request, _) => _workspace.CreateBotChatAsync(request));
        _connection.Handle(RemoteMethods.BotChatOpen, json.BotChatRefMessage, json.BotChatMessage,
            (request, _) => _workspace.OpenBotChatAsync(request.BotId, request.ChatId));
        _connection.Handle(RemoteMethods.BotChatClose, json.BotChatRefMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.CloseBotChatAsync(request.BotId, request.ChatId).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.BotChatRename, json.RenameBotChatMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.RenameBotChatAsync(request.BotId, request.ChatId, request.Title).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.BotChatDelete, json.BotChatRefMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.DeleteBotChatAsync(request.BotId, request.ChatId).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.BotItemResolve, json.ResolveBotItemMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.ResolveBotItemAsync(request).ConfigureAwait(false);
            return new Nothing();
        });
        _connection.Handle(RemoteMethods.BotConnectorToggle, json.ToggleBotConnectorMessage, json.Nothing, async (request, _) =>
        {
            await _workspace.ToggleBotConnectorAsync(request).ConfigureAwait(false);
            return new Nothing();
        });
    }

    private async Task<AcpSnapshotMessage> AttachConversationAsync(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) throw new RemoteException(RemoteProtocol.InvalidParams, "An item is needed.");
        // Attaching again replaces the earlier view (a device that saw a gap wants a fresh snapshot): the old one stops
        // first so that no update numbered past the new snapshot follows it.
        StopConversation(itemId);
        var subscriber = new ConversationSubscriber(this, itemId);
        IAcpAttachment attachment;
        try
        {
            attachment = await _workspace.AttachConversationAsync(itemId, subscriber.Updated).ConfigureAwait(false);
        }
        catch
        {
            subscriber.Dispose();
            throw;
        }
        long floor;
        lock (_conversations)
        {
            _conversations.Remove(itemId, out var replaced);
            if (replaced is not null)
            {
                var last = replaced.DisposeAndGetLastSequence();
                RecordSequenceLocked(itemId, last);
            }
            floor = _conversationSequences.GetValueOrDefault(itemId);
            _conversations[itemId] = subscriber;
        }
        var snapshot = subscriber.Begin(attachment, floor);
        // The connection ended while the workspace was busy: nothing will let go of the view later.
        if (_connection.Completion.IsCompleted) StopConversation(itemId);
        return snapshot;
    }

    private void StopConversation(string itemId)
    {
        ConversationSubscriber? subscriber;
        lock (_conversations)
        {
            if (!_conversations.Remove(itemId, out subscriber)) return;
        }
        var last = subscriber.DisposeAndGetLastSequence();
        lock (_conversations) RecordSequenceLocked(itemId, last);
    }

    private void RecordSequenceLocked(string itemId, long sequence)
    {
        if (sequence > _conversationSequences.GetValueOrDefault(itemId)) _conversationSequences[itemId] = sequence;
    }

    /// <summary>
    /// What one attached GUI conversation sends to the device: the snapshot as the answer to <c>acp/attach</c>, then
    /// each change as an <c>acp/update</c> numbered one after the other. The workspace only queues here, from whatever
    /// thread it is on, and never waits for the network.
    /// </summary>
    private sealed class ConversationSubscriber(HostConnection owner, string itemId) : IDisposable
    {
        private readonly Channel<AcpUpdateMessage> _queue = Channel.CreateUnbounded<AcpUpdateMessage>(new UnboundedChannelOptions { SingleReader = true });
        private readonly object _gate = new();
        private IAcpAttachment? _attachment;
        private long _sequence;
        private bool _disposed;

        public void Updated(AcpUpdateMessage update) => _queue.Writer.TryWrite(update);

        /// <summary>The attachment exists: what was queued since goes out after the snapshot, numbered from it on.</summary>
        public AcpSnapshotMessage Begin(IAcpAttachment attachment, long sequenceFloor)
        {
            var snapshot = attachment.Snapshot;
            snapshot.ItemId = itemId;
            bool disposed;
            lock (_gate)
            {
                _attachment = attachment;
                snapshot.Sequence = Math.Max(snapshot.Sequence, sequenceFloor);
                _sequence = snapshot.Sequence;
                disposed = _disposed;
            }
            if (disposed) attachment.Dispose();
            else _ = Task.Run(SendLoopAsync);
            return snapshot;
        }

        public void Dispose() => DisposeAndGetLastSequence();

        public long DisposeAndGetLastSequence()
        {
            IAcpAttachment? attachment;
            long last;
            lock (_gate)
            {
                last = _sequence;
                if (_disposed) return last;
                _disposed = true;
                attachment = _attachment;
            }
            _queue.Writer.TryComplete();
            attachment?.Dispose();
            return last;
        }

        private async Task SendLoopAsync()
        {
            try
            {
                await foreach (var update in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    long sequence;
                    lock (_gate)
                    {
                        if (_disposed) break;
                        sequence = ++_sequence;
                    }
                    // A copy: the workspace may hand the same update to every device that looks at the conversation.
                    var numbered = new AcpUpdateMessage
                    {
                        ItemId = itemId,
                        Sequence = sequence,
                        Upserts = update.Upserts,
                        Removed = update.Removed,
                        State = update.State,
                        StatusDetail = update.StatusDetail,
                        ErrorText = update.ErrorText,
                        QueuedCount = update.QueuedCount,
                        Composer = update.Composer,
                        Tasks = update.Tasks,
                    };
                    await owner._connection.NotifyAsync(RemoteMethods.AcpUpdate, numbered, RemoteJsonContext.Default.AcpUpdateMessage).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The connection ended; its completion releases everything.
            }
        }
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
            var attachment = await _workspace.AttachAsync(request.ItemId, request.Columns, request.Rows, native, request.ClaimSize, subscriber, request.ResumeFrom).ConfigureAwait(false);
            var snapshotLength = attachment.SnapshotLength;
            var resumed = attachment.Resumed;
            subscriber.Begin(attachment);
            return new AttachedMessage
            {
                Channel = request.Channel,
                Position = attachment.Position,
                Snapshot = attachment.IsNative ? SnapshotFormats.Native : SnapshotFormats.Vt,
                SnapshotLength = snapshotLength,
                Resumed = resumed,
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
        switch (header.Type)
        {
            case FrameType.Input when _subscribers.TryGetValue(header.Channel, out var subscriber):
                subscriber.Attachment?.Input(payload.Span);
                break;
            case FrameType.Ack when payload.Length >= 8 && _subscribers.TryGetValue(header.Channel, out var subscriber):
                subscriber.Acknowledge(BinaryPrimitives.ReadUInt64LittleEndian(payload.Span));
                break;
            case FrameType.StreamWindow when payload.Length >= 8 && _streamSenders.TryGetValue(header.Channel, out var sender):
                sender.Advance(BinaryPrimitives.ReadUInt64LittleEndian(payload.Span));
                break;
            case FrameType.StreamData when _streamReceivers.TryGetValue(header.Channel, out var receiver):
                receiver.OnData(payload);
                break;
            case FrameType.StreamEnd when _streamReceivers.TryGetValue(header.Channel, out var receiver):
                receiver.OnEnd(payload.Length > 0 ? payload.Span[0] : (byte)0);
                break;
        }
    }

    private async Task<ReadStreamReadyMessage> StartReadStreamAsync(ReadStreamMessage request)
    {
        if (request.Channel == 0) throw new RemoteException(RemoteProtocol.InvalidParams, "A stream channel is needed.");
        var window = new StreamSendWindow();
        if (!_streamSenders.TryAdd(request.Channel, window)) throw new RemoteException(RemoteProtocol.InvalidParams, "The channel is in use.");
        try
        {
            var (firstChunk, size) = await _workspace.ReadAsync(request.Path, 0, 0).ConfigureAwait(false);
            _ = firstChunk;
            if (request.MaxBytes > 0 && size > request.MaxBytes)
            {
                _streamSenders.TryRemove(request.Channel, out _);
                return new ReadStreamReadyMessage { Size = size };
            }
            _ = Task.Run(() => PumpReadStreamAsync(request.Path, request.Channel, size, window));
            return new ReadStreamReadyMessage { Size = size };
        }
        catch
        {
            _streamSenders.TryRemove(request.Channel, out _);
            throw;
        }
    }

    private async Task PumpReadStreamAsync(string path, uint channel, long size, StreamSendWindow window)
    {
        try
        {
            var chunkSize = Math.Clamp(_connection.MaxChunkBytes, 16 * 1024, RemoteMethods.FilePieceBytes);
            long offset = 0;
            while (offset < size && !window.IsAborted)
            {
                var length = (int)Math.Min(chunkSize, size - offset);
                if (!await window.WaitForCreditAsync((ulong)offset, length).ConfigureAwait(false)) return;
                var (data, _) = await _workspace.ReadAsync(path, offset, length).ConfigureAwait(false);
                if (data.Length == 0) break;
                await _connection.SendDataAsync(FrameType.StreamData, channel, (ReadOnlyMemory<byte>)data).ConfigureAwait(false);
                offset += data.Length;
            }
            await _connection.SendDataAsync(FrameType.StreamEnd, channel, new byte[] { 0 }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is RemoteException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            try { await _connection.SendDataAsync(FrameType.StreamEnd, channel, new byte[] { 1 }).ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            _streamSenders.TryRemove(channel, out _);
        }
    }

    private async Task<Nothing> StartWriteStreamAsync(WriteStreamMessage request)
    {
        if (request.Channel == 0) throw new RemoteException(RemoteProtocol.InvalidParams, "A stream channel is needed.");
        var upload = Guid.NewGuid().ToString("N");
        // Verify path permissions upfront before opening the stream channel.
        if (request.Size == 0)
        {
            await _workspace.WriteAsync(request.Path, upload, [], last: false).ConfigureAwait(false);
        }
        var receiver = new StreamWriteReceiver(this, request.Channel,
            chunk => _workspace.WriteAsync(request.Path, upload, chunk, last: false),
            async () =>
            {
                await _workspace.WriteAsync(request.Path, upload, [], last: true).ConfigureAwait(false);
                return null;
            });
        if (!_streamReceivers.TryAdd(request.Channel, receiver))
        {
            receiver.Abort();
            throw new RemoteException(RemoteProtocol.InvalidParams, "The channel is in use.");
        }
        return new Nothing();
    }

    private async Task<Nothing> FinishWriteStreamAsync(uint channel)
    {
        if (!_streamReceivers.TryRemove(channel, out var receiver))
        {
            throw new RemoteException(RemoteMethods.NotFound, "No write stream is active on this channel.");
        }
        await receiver.FinishAsync().ConfigureAwait(false);
        return new Nothing();
    }

    private Task<Nothing> StartUploadStreamAsync(UploadStreamMessage request)
    {
        if (request.Channel == 0) throw new RemoteException(RemoteProtocol.InvalidParams, "A stream channel is needed.");
        if (request.Size > RemoteMethods.UploadMaxBytes) throw new RemoteException(RemoteProtocol.InvalidParams, "The picture is too large.");
        var upload = Guid.NewGuid().ToString("N");
        var receiver = new StreamWriteReceiver(this, request.Channel,
            chunk => _workspace.UploadAsync(request.ItemId, upload, request.Name, chunk, last: false),
            () => _workspace.UploadAsync(request.ItemId, upload, request.Name, [], last: true));
        if (!_streamReceivers.TryAdd(request.Channel, receiver))
        {
            receiver.Abort();
            throw new RemoteException(RemoteProtocol.InvalidParams, "The channel is in use.");
        }
        return Task.FromResult(new Nothing());
    }

    private async Task<UploadedMessage> FinishUploadStreamAsync(uint channel)
    {
        if (!_streamReceivers.TryRemove(channel, out var receiver))
        {
            throw new RemoteException(RemoteMethods.NotFound, "No upload stream is active on this channel.");
        }
        var path = await receiver.FinishAsync().ConfigureAwait(false);
        return new UploadedMessage { Path = path };
    }

    /// <summary>Tracks the receiver's sliding window for an outbound <see cref="RemoteMethods.FsReadStream"/>.</summary>
    private sealed class StreamSendWindow
    {
        private readonly object _gate = new();
        private ulong _acknowledged;
        private TaskCompletionSource? _waiter;
        private bool _aborted;

        public bool IsAborted => Volatile.Read(ref _aborted);

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
            wake?.TrySetResult();
        }

        public async ValueTask<bool> WaitForCreditAsync(ulong sentSoFar, int nextBytes)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_aborted) return false;
                    if (sentSoFar + (ulong)nextBytes - _acknowledged <= RemoteMethods.StreamWindowBytes) return true;
                    _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _waiter.Task;
                }
                await wait.ConfigureAwait(false);
            }
        }
    }

    /// <summary>Receives <see cref="FrameType.StreamData"/> chunks on a channel and writes them in order.</summary>
    private sealed class StreamWriteReceiver
    {
        private readonly HostConnection _owner;
        private readonly uint _channel;
        private readonly Func<byte[], Task> _writeChunk;
        private readonly Func<Task<string?>> _commit;
        private readonly Channel<byte[]?> _chunks = Channel.CreateUnbounded<byte[]?>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task<string?> _processing;
        private byte _endStatus;

        public StreamWriteReceiver(HostConnection owner, uint channel, Func<byte[], Task> writeChunk, Func<Task<string?>> commit)
        {
            _owner = owner;
            _channel = channel;
            _writeChunk = writeChunk;
            _commit = commit;
            _processing = Task.Run(ProcessLoopAsync);
        }

        public void OnData(ReadOnlyMemory<byte> payload) => _chunks.Writer.TryWrite(payload.ToArray());

        public void OnEnd(byte status)
        {
            _endStatus = status;
            _chunks.Writer.TryComplete();
        }

        public void Abort()
        {
            _endStatus = 1;
            _chunks.Writer.TryComplete();
        }

        public Task<string?> FinishAsync()
        {
            _chunks.Writer.TryComplete();
            return _processing;
        }

        private async Task<string?> ProcessLoopAsync()
        {
            ulong consumed = 0;
            ulong acknowledged = 0;
            await foreach (var chunk in _chunks.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (chunk is null) break;
                if (chunk.Length > 0)
                {
                    await _writeChunk(chunk).ConfigureAwait(false);
                    consumed += (ulong)chunk.Length;
                    if (consumed - acknowledged >= RemoteMethods.StreamAckEveryBytes)
                    {
                        acknowledged = consumed;
                        var win = new byte[8];
                        BinaryPrimitives.WriteUInt64LittleEndian(win, acknowledged);
                        try { await _owner._connection.SendDataAsync(FrameType.StreamWindow, _channel, (ReadOnlyMemory<byte>)win).ConfigureAwait(false); }
                        catch { }
                    }
                }
            }
            if (_endStatus != 0) throw new RemoteException(RemoteProtocol.InvalidParams, "The binary stream was aborted.");
            return await _commit().ConfigureAwait(false);
        }
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
            var resumed = attachment.Resumed;
            var snapshot = resumed ? [] : attachment.TakeSnapshot();
            _ = Task.Run(() => SendLoopAsync(snapshot, resumed));
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

        private async Task SendLoopAsync(byte[] snapshot, bool resumed)
        {
            try
            {
                var connection = owner._connection;
                if (!resumed)
                {
                    var pieceSize = Math.Clamp(connection.MaxChunkBytes, 16 * 1024, SnapshotPiece);
                    for (var offset = 0; ; offset += pieceSize)
                    {
                        var length = Math.Min(pieceSize, snapshot.Length - offset);
                        var last = offset + length >= snapshot.Length;
                        var piece = new byte[1 + length];
                        piece[0] = last ? (byte)1 : (byte)0;
                        snapshot.AsSpan(offset, length).CopyTo(piece.AsSpan(1));
                        await connection.SendDataAsync(FrameType.Snapshot, channel, piece).ConfigureAwait(false);
                        if (last) break;
                    }
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
