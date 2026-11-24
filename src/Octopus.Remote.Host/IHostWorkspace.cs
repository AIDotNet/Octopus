using Octopus.Remote.Protocol;

namespace Octopus.Remote.Host;

/// <summary>
/// This computer's projects, items and terminals as a connected device may use them. The app implements it; calls
/// arrive on any thread and the implementation goes to wherever its own state lives. An id that does not exist is
/// answered with a <see cref="RemoteException"/> of <see cref="RemoteMethods.NotFound"/>.
/// </summary>
public interface IHostWorkspace
{
    HostInfoMessage Info { get; }
    Task<IReadOnlyList<ProjectMessage>> ListProjectsAsync();
    /// <summary>Adds a folder of this computer as a project, or returns the project that folder already is.</summary>
    Task<ProjectMessage> CreateProjectAsync(CreateProjectMessage request);
    Task<IReadOnlyList<ItemMessage>> ListItemsAsync(string projectId);
    /// <summary>Adds a terminal or agent to a project without bringing it to the front of this computer's window.</summary>
    Task<ItemMessage> CreateItemAsync(CreateItemMessage request);
    Task RemoveItemAsync(string itemId);
    Task RenameItemAsync(string itemId, string? title);
    /// <summary>Returns git branches and optional GitHub issues/PRs available for creating a branch workspace in a project.</summary>
    Task<WorkspaceOptionsMessage> GetWorkspaceOptionsAsync(string projectId) => Task.FromResult(new WorkspaceOptionsMessage());
    /// <summary>Creates an isolated git worktree workspace under <c>~/.octopus/workspaces/&lt;project&gt;/&lt;branch&gt;</c> on this host.</summary>
    Task<WorkspaceMessage> CreateWorkspaceAsync(CreateWorkspaceMessage request) =>
        throw new RemoteException(RemoteProtocol.MethodNotFound, "Branch workspaces are not supported by this host.");
    Task<IReadOnlyList<AgentMessage>> ListAgentsAsync();

    /// <summary>
    /// Shows an item's terminal to a device: starts its program when it does not run yet (in a terminal of the given
    /// size), takes the terminal's state and reports everything after it to <paramref name="sink"/>.
    /// </summary>
    Task<ISessionAttachment> AttachAsync(string itemId, int columns, int rows, bool nativeSnapshot, bool claimSize, ISessionSink sink);
    Task RestartAsync(string itemId);

    // Files. A path outside every project's folder is answered with RemoteMethods.NotAllowed.
    Task<string> ResolveAsync(string path);
    Task<IReadOnlyList<FileEntryMessage>> ListAsync(string directory);
    Task<bool> ExistsAsync(string path);
    /// <summary>A piece of a file and the file's whole size.</summary>
    Task<(byte[] Data, long Size)> ReadAsync(string path, long offset, int length);
    /// <summary>One piece of a write; the file is replaced when <paramref name="last"/> arrives.</summary>
    Task WriteAsync(string path, string upload, byte[] data, bool last);
    Task CreateFileAsync(string path);
    Task CreateDirectoryAsync(string path);
    Task RenameAsync(string path, string newPath);
    Task DeleteAsync(string path, bool isDirectory);
    /// <summary>Calls <paramref name="changed"/> (on any thread) whenever something under the folder changes, until the result is disposed.</summary>
    Task<IDisposable> WatchAsync(string folder, Action changed);
    /// <summary>Lists folders anywhere on the host (directories only, no files) for choosing a project folder.</summary>
    Task<BrowseFolderResultMessage> BrowseFolderAsync(BrowseFolderMessage request);
    /// <summary>Creates an empty folder inside <see cref="CreateBrowseFolderMessage.ParentPath"/> and returns its full path.</summary>
    Task<string> BrowseCreateDirectoryAsync(CreateBrowseFolderMessage request);
    /// <summary>A call of this computer's git service on a repository inside a project: arguments and result as JSON.</summary>
    Task<string> GitAsync(string method, string arguments);

    // GUI conversations (AcpMessages.cs). A host that shows none refuses every call; the app's workspace overrides them.

    /// <summary>
    /// Shows a GUI conversation to a device: returns how it is now and reports every later change to
    /// <paramref name="updated"/> (on any thread, in order, without <see cref="AcpUpdateMessage.Sequence"/>, which the
    /// connection numbers) until the result is disposed. An item that is not a GUI conversation is answered with
    /// <see cref="RemoteMethods.NotAllowed"/>.
    /// </summary>
    Task<IAcpAttachment> AttachConversationAsync(string itemId, Action<AcpUpdateMessage> updated) => throw NoConversations();
    /// <summary>Sends a prompt as if typed at this computer; queued by the conversation while a turn runs.</summary>
    Task PromptAsync(AcpPromptMessage prompt) => throw NoConversations();
    /// <summary>Stops the turn that runs, as the stop button does.</summary>
    Task CancelTurnAsync(string itemId) => throw NoConversations();
    /// <summary>Answers a permission or question card; a card answered already (here or on another device) stays as it is.</summary>
    Task RespondAsync(AcpRespondMessage answer) => throw NoConversations();
    /// <summary>Chooses a model, mode or effort above the input.</summary>
    Task SetConversationOptionAsync(AcpSetOptionMessage option) => throw NoConversations();

    private static RemoteException NoConversations() => new(RemoteMethods.NotAllowed, "This host shows no GUI conversations.");

    /// <summary>Projects or their items were added, removed or reordered.</summary>
    event Action? Changed;
    /// <summary>One item's title or activity changed.</summary>
    event Action<ItemMessage>? ItemChanged;
}

/// <summary>One device's view of one GUI conversation. Disposing it ends the reports.</summary>
public interface IAcpAttachment : IDisposable
{
    /// <summary>The conversation when the view began; the first report is the first change after it.</summary>
    AcpSnapshotMessage Snapshot { get; }
}

/// <summary>
/// Where an attached terminal reports to. Called on the thread that reads the program's output, with the terminal's
/// lock held: copy and return. Positions are those of the terminal's output (one per byte, one per resize or reset).
/// </summary>
public interface ISessionSink
{
    void Output(ulong position, ReadOnlySpan<byte> bytes);
    void Resized(ulong position, int columns, int rows);
    void Reset(ulong position);
    void Exited(int? exitCode);
}

/// <summary>One device's view of one terminal. Disposing it ends the reports and gives up the say over the size.</summary>
public interface ISessionAttachment : IDisposable
{
    /// <summary>The position of the first output after <see cref="Snapshot"/>.</summary>
    ulong Position { get; }
    bool IsNative { get; }
    int SnapshotLength { get; }
    /// <summary>Takes the initial snapshot once; subsequent calls return an empty array so the buffer is not kept for the whole session.</summary>
    byte[] TakeSnapshot();
    int Columns { get; }
    int Rows { get; }
    string? Title { get; }
    bool IsRunning { get; }
    int? ExitCode { get; }
    /// <summary>True while this view decides the terminal's size.</summary>
    bool OwnsSize { get; }
    void Input(ReadOnlySpan<byte> bytes);
    /// <summary>Asks for another size; applied only when this view has the say, or takes it with <paramref name="claim"/>.</summary>
    (bool Applied, int Columns, int Rows) Resize(int columns, int rows, bool claim);
    /// <summary>Gives up the say over the size while still looking; returns the size the terminal has afterwards.</summary>
    (int Columns, int Rows) ReleaseSize();
}
