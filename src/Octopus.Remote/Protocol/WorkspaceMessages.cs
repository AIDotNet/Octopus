namespace Octopus.Remote.Protocol;

/// <summary>The methods and events of a host's workspace (design/12-远程操作.md §6.2). The device calls, the host answers; events go from the host.</summary>
public static class RemoteMethods
{
    public const string HostInfo = "host/info";
    public const string ProjectList = "project/list";
    public const string ProjectCreate = "project/create";
    /// <summary>Fetches the custom thumbnail PNG of a project whose <see cref="ProjectMessage.Image"/> is set.</summary>
    public const string ProjectIcon = "project/icon";
    public const string ItemList = "item/list";
    public const string ItemCreate = "item/create";
    public const string ItemRemove = "item/remove";
    public const string ItemRename = "item/rename";
    public const string WorkspaceOptions = "workspace/options";
    public const string WorkspaceCreate = "workspace/create";
    public const string AgentCatalog = "agent/catalog";
    public const string SessionAttach = "session/attach";
    public const string SessionDetach = "session/detach";
    public const string SessionResize = "session/resize";
    /// <summary>Gives up the say over a terminal's size while still showing it (design/12 §6.5): the view that had it before decides again.</summary>
    public const string SessionRelease = "session/release";
    public const string SessionRestart = "session/restart";

    // Files of the host's projects (design/12 §6.2). Every path must lie inside a project's folder.
    public const string FsResolve = "fs/resolve";
    public const string FsList = "fs/list";
    public const string FsExists = "fs/exists";
    public const string FsRead = "fs/read";
    public const string FsWrite = "fs/write";
    public const string FsCreateFile = "fs/createFile";
    public const string FsCreateDirectory = "fs/createDirectory";
    public const string FsRename = "fs/rename";
    public const string FsDelete = "fs/delete";
    /// <summary>Asks to be told when anything under a folder changes (<see cref="FsChanged"/>, with the same path).</summary>
    public const string FsWatch = "fs/watch";
    public const string FsUnwatch = "fs/unwatch";
    /// <summary>Lists folders (only directories, never files) anywhere on the host for picking a new project folder.</summary>
    public const string FsBrowse = "fs/browse";
    /// <summary>Creates a new empty folder while browsing for a new project folder.</summary>
    public const string FsBrowseCreateDirectory = "fs/browseCreateDirectory";
    /// <summary>
    /// Hands the host a picture for a session (<see cref="UploadMessage"/>, piece by piece): it is kept in the host's own
    /// temporary folder, not a project's, and the last piece is answered with its path for the session to be given.
    /// </summary>
    public const string FsUpload = "fs/upload";
    /// <summary>Streams a file from the host over a binary channel without Base64 overhead (v2).</summary>
    public const string FsReadStream = "fs/readStream";
    /// <summary>Opens a binary channel to stream a file write to the host (v2).</summary>
    public const string FsWriteStream = "fs/writeStream";
    /// <summary>Completes a streamed file write and atomically replaces the target file (v2).</summary>
    public const string FsWriteFinish = "fs/writeFinish";
    /// <summary>Opens a binary channel to stream a session image upload to the host (v2).</summary>
    public const string FsUploadStream = "fs/uploadStream";
    /// <summary>Completes a streamed session image upload and returns its path on the host (v2).</summary>
    public const string FsUploadFinish = "fs/uploadFinish";
    /// <summary>Exchanges ICE/STUN candidates and credentials to punch a direct WebRTC P2P UDP channel (v2).</summary>
    public const string TransportP2pHandshake = "transport/p2pHandshake";
    /// <summary>Event: the sender is migrating this connection to a newly established transport channel (v2).</summary>
    public const string TransportMigrating = "transport/migrating";
    /// <summary>A call of the host's git service: the method's name and its arguments as JSON, the result as JSON.</summary>
    public const string GitCall = "git/call";

    // System telemetry, process signaling, and controlled remote execution.
    public const string SystemMetrics = "system/metrics";
    public const string SystemSignalProcess = "system/signalProcess";
    public const string ProcessRun = "process/run";

    /// <summary>Event: something under a watched folder changed.</summary>
    public const string FsChanged = "fs/changed";
    // A GUI conversation (AcpMessages.cs).
    public const string AcpAttach = "acp/attach";
    public const string AcpDetach = "acp/detach";
    public const string AcpPrompt = "acp/prompt";
    public const string AcpCancel = "acp/cancel";
    public const string AcpRespond = "acp/respond";
    public const string AcpSetOption = "acp/setOption";
    /// <summary>Event: an attached GUI conversation changed.</summary>
    public const string AcpUpdate = "acp/update";

    /// <summary>Event: projects or their items changed; the device reads the lists again.</summary>
    public const string WorkspaceChanged = "workspace/changed";
    /// <summary>Event: one item's title or activity changed.</summary>
    public const string ItemChanged = "item/changed";
    /// <summary>Event: the program of an attached terminal ended.</summary>
    public const string SessionExited = "session/exited";
    /// <summary>Event: the device fell too far behind; the host stopped sending and the device attaches again for a fresh snapshot.</summary>
    public const string SessionResync = "session/resync";

    /// <summary>Capabilities named in <c>hello</c>.</summary>
    public const string CapabilityWorkspace = "workspace";
    public const string CapabilityNativeSnapshot = "snapshot.native";
    public const string CapabilityFiles = "files";
    public const string CapabilityGit = "git";
    /// <summary>The host knows <see cref="SessionRelease"/>.</summary>
    public const string CapabilitySizeRelease = "session.release";
    /// <summary>The host knows <see cref="FsBrowse"/> and <see cref="FsBrowseCreateDirectory"/>.</summary>
    public const string CapabilityBrowse = "fs.browse";
    /// <summary>The host shows its GUI conversations (<see cref="AcpAttach"/> and the rest of <c>acp/*</c>).</summary>
    public const string CapabilityAcpGui = "acp.gui";
    /// <summary>The host exposes branch workspaces (<see cref="WorkspaceOptions"/>, <see cref="WorkspaceCreate"/>, and workspace ids on items).</summary>
    public const string CapabilityWorkspaces = "workspaces";
    /// <summary>The host takes pictures for its sessions (<see cref="FsUpload"/>).</summary>
    public const string CapabilityUpload = "fs.upload";
    /// <summary>The host answers <see cref="SystemMetrics"/>.</summary>
    public const string CapabilitySystemMetrics = "system.metrics";
    /// <summary>The host answers <see cref="SystemSignalProcess"/>.</summary>
    public const string CapabilitySystemSignal = "system.signal";
    /// <summary>The host answers <see cref="ProcessRun"/>.</summary>
    public const string CapabilityProcessRun = "process.run";
    /// <summary>The peer speaks protocol v2 extensions (negotiation, ping/pong, priority frames).</summary>
    public const string CapabilityProtocolV2 = "protocol.v2";
    /// <summary>The peer supports adaptive frame payload compression (<see cref="FrameFlags.Compressed"/>).</summary>
    public const string CapabilityCompression = "frame.compression";
    /// <summary>The peer supports binary file and image streaming (<see cref="FsReadStream"/>, <see cref="FsWriteStream"/>, <see cref="FsUploadStream"/>).</summary>
    public const string CapabilityFileStream = "fs.stream";
    /// <summary>The host keeps a 1 MiB ring buffer per attached terminal and supports <see cref="AttachMessage.ResumeFrom"/>.</summary>
    public const string CapabilitySessionResume = "session.resume";
    /// <summary>The peer supports QUIC transport (<c>quic://</c>).</summary>
    public const string CapabilityTransportQuic = "transport.quic";
    /// <summary>The peer supports WebRTC P2P ICE-UDP direct hole-punching transport (<see cref="TransportP2pHandshake"/>).</summary>
    public const string CapabilityTransportP2p = "transport.p2p";
    /// <summary>The peer supports live transport migration without dropping sessions (<see cref="TransportMigrating"/>).</summary>
    public const string CapabilityTransportMigrate = "transport.migrate";
    /// <summary>The host exposes custom project thumbnail images (<see cref="ProjectMessage.Image"/> and <see cref="ProjectIcon"/>).</summary>
    public const string CapabilityProjectIcons = "project.icons";

    /// <summary>Maximum byte size of a single project thumbnail returned by <see cref="ProjectIcon"/>.</summary>
    public const int ProjectIconMaxBytes = 512 * 1024;

    /// <summary>The most bytes of a file in one <see cref="FsRead"/> answer or <see cref="FsWrite"/> request.</summary>
    public const int FilePieceBytes = 256 * 1024;
    /// <summary>The chunk size of a binary stream frame (<see cref="FrameType.StreamData"/>) in protocol v2.</summary>
    public const int StreamChunkBytes = 64 * 1024;
    /// <summary>Maximum unacknowledged in-flight bytes on a binary stream channel before the sender pauses for <see cref="FrameType.StreamWindow"/>.</summary>
    public const int StreamWindowBytes = 512 * 1024;
    /// <summary>How often a binary stream receiver sends a <see cref="FrameType.StreamWindow"/> update.</summary>
    public const int StreamAckEveryBytes = 128 * 1024;
    /// <summary>The most bytes of one picture sent with <see cref="FsUpload"/> or <see cref="FsUploadStream"/>.</summary>
    public const long UploadMaxBytes = 20 * 1024 * 1024;

    public const int NotFound = -33010;
    public const int NotAllowed = -33002;
    public const int StartFailed = -33020;
}

/// <summary>What a terminal's record frame says (after its 8 bytes of position).</summary>
public static class RecordKinds
{
    /// <summary>The grid changed: two bytes of columns, two of rows, little-endian.</summary>
    public const byte Resize = 1;
    /// <summary>The terminal was cleared for a restart of its program.</summary>
    public const byte Reset = 2;
}

/// <summary>What an item is doing, as the host's own activity tracking sees it.</summary>
public static class ItemActivities
{
    public const string Idle = "idle";
    public const string Working = "working";
    public const string Waiting = "waiting";
    public const string Error = "error";
}

public static class ItemKinds
{
    public const string Terminal = "terminal";
    public const string Agent = "agent";
}

/// <summary>How an agent item runs on the host (<see cref="ItemMessage.Surface"/>).</summary>
public static class ItemSurfaces
{
    /// <summary>A program in a terminal: the device can attach to it.</summary>
    public const string Terminal = "terminal";
    /// <summary>An ACP conversation in the host's GUI tab: there is no terminal to attach to.</summary>
    public const string Gui = "gui";
}

public static class SnapshotFormats
{
    public const string Vt = "vt";
    public const string Native = "native";
}

/// <summary>Parameters or a result with nothing in it.</summary>
public sealed class Nothing;

public sealed class HostInfoMessage
{
    public string Name { get; set; } = "";
    /// <summary><c>macos</c>, <c>windows</c> or <c>linux</c>.</summary>
    public string Os { get; set; } = "";
    public string AppVersion { get; set; } = "";
    /// <summary>The home folder, where "new project" starts looking.</summary>
    public string Home { get; set; } = "";
}

public sealed class ProjectMessage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The folder on the host.</summary>
    public string Path { get; set; } = "";
    public string? Color { get; set; }
    public string? Icon { get; set; }
    /// <summary>Versioned file name of the project's custom picture thumbnail on the host; null when using an icon or initial.</summary>
    public string? Image { get; set; }
    /// <summary>True when the project's folder sits inside a git repository on the host.</summary>
    public bool IsGitRepository { get; set; }
    /// <summary>Current or default branch of the project's main directory, when it is a git repository.</summary>
    public string? DefaultBranch { get; set; }
    /// <summary>Isolated branch worktree workspaces belonging to this project.</summary>
    public List<WorkspaceMessage>? Workspaces { get; set; }
}

/// <summary>One isolated git worktree workspace of a project on the host.</summary>
public sealed class WorkspaceMessage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary><c>issue</c>, <c>pr</c>, <c>url</c> or <c>note</c>; null when no task is linked.</summary>
    public string? TaskKind { get; set; }
    public string? TaskKey { get; set; }
    public string? TaskTitle { get; set; }
    public string? TaskUrl { get; set; }
}

public sealed class WorkspaceBranchMessage
{
    public string Name { get; set; } = "";
    public bool IsRemote { get; set; }
    public bool IsCurrent { get; set; }
    /// <summary>Where this branch is already checked out, or null when free to use in a new worktree.</summary>
    public string? OccupiedBy { get; set; }
}

public sealed class WorkspaceTaskOptionMessage
{
    /// <summary><c>issue</c> or <c>pr</c>.</summary>
    public string Kind { get; set; } = "issue";
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    public string? Body { get; set; }
    public string SuggestedBranch { get; set; } = "";
    public string? HeadBranch { get; set; }
}

public sealed class WorkspaceOptionsMessage
{
    public bool IsGitRepository { get; set; }
    public string? DefaultBranch { get; set; }
    /// <summary>Formatted directory on the host where new worktrees for this project will be placed; null falls back to default <c>~/.octopus/workspaces/&lt;project&gt;</c>.</summary>
    public string? WorkspacesDirectory { get; set; }
    public List<WorkspaceBranchMessage> Branches { get; set; } = [];
    public bool GhAvailable { get; set; }
    public List<WorkspaceTaskOptionMessage> Issues { get; set; } = [];
    public List<WorkspaceTaskOptionMessage> PullRequests { get; set; } = [];
}

public sealed class CreateWorkspaceMessage
{
    public string ProjectId { get; set; } = "";
    public bool CreateBranch { get; set; } = true;
    public string BranchName { get; set; } = "";
    public string? BaseRef { get; set; }
    public bool IsRemoteBranch { get; set; }
    /// <summary><c>issue</c>, <c>pr</c>, <c>url</c> or <c>note</c>; null when no task is linked.</summary>
    public string? TaskKind { get; set; }
    public string? TaskKey { get; set; }
    public string? TaskTitle { get; set; }
    public string? TaskUrl { get; set; }
    public string? TaskBody { get; set; }
}

public sealed class ProjectListMessage
{
    public List<ProjectMessage> Projects { get; set; } = [];
}

public sealed class CreateProjectMessage
{
    /// <summary>A folder on the host; <c>~</c> stands for its home.</summary>
    public string Path { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>Make the folder when it does not exist; otherwise a missing folder is an error.</summary>
    public bool CreateFolder { get; set; }
}

public sealed class ProjectRefMessage
{
    public string ProjectId { get; set; } = "";
}

/// <summary>The custom picture thumbnail of a project (<see cref="RemoteMethods.ProjectIcon"/>).</summary>
public sealed class ProjectIconMessage
{
    /// <summary>Versioned file name matching <see cref="ProjectMessage.Image"/>; null when the project has no picture.</summary>
    public string? Image { get; set; }
    /// <summary>PNG image bytes encoded as base64; null when unavailable.</summary>
    public string? Data { get; set; }
}

public sealed class ItemMessage
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    /// <summary>One of <see cref="ItemKinds"/>.</summary>
    public string Kind { get; set; } = ItemKinds.Terminal;
    public string Title { get; set; } = "";
    public string? AgentId { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    /// <summary>Null when the item runs in the project's default folder; otherwise the id of its branch workspace.</summary>
    public string? WorkspaceId { get; set; }
    /// <summary>The branch name of the item's workspace, when it belongs to one.</summary>
    public string? WorkspaceBranch { get; set; }
    /// <summary>One of <see cref="ItemActivities"/>.</summary>
    public string Activity { get; set; } = ItemActivities.Idle;
    /// <summary>True when its program runs (a tab that was never shown has none yet).</summary>
    public bool IsRunning { get; set; }
    /// <summary>
    /// One of <see cref="ItemSurfaces"/>. Null from a host that predates it, and for a plain terminal: both mean a
    /// terminal. A <see cref="ItemSurfaces.Gui"/> item refuses <see cref="RemoteMethods.SessionAttach"/> and
    /// <see cref="RemoteMethods.SessionRestart"/>.
    /// </summary>
    public string? Surface { get; set; }
}

public sealed class ItemListMessage
{
    public List<ItemMessage> Items { get; set; } = [];
}

public sealed class CreateItemMessage
{
    public string ProjectId { get; set; } = "";
    public string Kind { get; set; } = ItemKinds.Terminal;
    public string? AgentId { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? Prompt { get; set; }
    /// <summary>Null or empty starts in the project's default folder; otherwise starts inside that branch workspace.</summary>
    public string? WorkspaceId { get; set; }
    /// <summary>When true and the target workspace has a linked task, prepend its context to the agent's initial prompt.</summary>
    public bool InjectTaskPrompt { get; set; }
    /// <summary>Optional requested surface (<see cref="ItemSurfaces.Gui"/> or <see cref="ItemSurfaces.Terminal"/>); null keeps host default behavior.</summary>
    public string? Surface { get; set; }
}

public sealed class ItemRefMessage
{
    public string ItemId { get; set; } = "";
}

public sealed class RenameItemMessage
{
    public string ItemId { get; set; } = "";
    /// <summary>Null or empty goes back to the item's own title.</summary>
    public string? Title { get; set; }
}

/// <summary>An agent that is installed and enabled on the host, with what can be chosen when starting it.</summary>
public sealed class AgentMessage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<AgentChoiceMessage> Models { get; set; } = [];
    public bool AllowCustomModel { get; set; }
    /// <summary>What the host calls the effort setting of this agent ("Effort", "Reasoning effort"); null when it has none.</summary>
    public string? EffortLabel { get; set; }
    public List<AgentChoiceMessage> Efforts { get; set; } = [];
    public bool TakesPrompt { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>True when this agent supports ACP GUI conversations on the host.</summary>
    public bool SupportsAcp { get; set; }
    /// <summary>Effective default surface configured on the host (<see cref="ItemSurfaces.Gui"/> or <see cref="ItemSurfaces.Terminal"/>).</summary>
    public string? DefaultSurface { get; set; }
}

public sealed class AgentChoiceMessage
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class AgentListMessage
{
    public List<AgentMessage> Agents { get; set; } = [];
}

public sealed class AttachMessage
{
    public string ItemId { get; set; } = "";
    /// <summary>
    /// The channel the terminal's frames will use, chosen by the device (any number it does not use on this connection,
    /// not 0): it can be listening before the first frame arrives.
    /// </summary>
    public uint Channel { get; set; }
    /// <summary>The device's grid; used to start the program when it does not run yet, and as the size when <see cref="ClaimSize"/>.</summary>
    public int Columns { get; set; }
    public int Rows { get; set; }
    /// <summary>One of <see cref="SnapshotFormats"/>.</summary>
    public string Snapshot { get; set; } = SnapshotFormats.Vt;
    /// <summary>Make this device's view decide the terminal's size (design/12 §6.5).</summary>
    public bool ClaimSize { get; set; }
    /// <summary>
    /// When re-attaching after a drop, the position up to which the device already processed output. If the host still
    /// has everything from there in its ring buffer, it skips the snapshot and resumes from that position (v2).
    /// </summary>
    public ulong? ResumeFrom { get; set; }
}

public sealed class AttachedMessage
{
    /// <summary>The channel the terminal's frames use on this connection.</summary>
    public uint Channel { get; set; }
    /// <summary>The position of the first output after the snapshot (or the position resumed from when <see cref="Resumed"/> is true).</summary>
    public ulong Position { get; set; }
    public string Snapshot { get; set; } = SnapshotFormats.Vt;
    public int SnapshotLength { get; set; }
    /// <summary>True when the host resumed from <see cref="AttachMessage.ResumeFrom"/> without sending a <see cref="FrameType.Snapshot"/>.</summary>
    public bool Resumed { get; set; }
    public int Columns { get; set; }
    public int Rows { get; set; }
    public string? Title { get; set; }
    public bool IsRunning { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>True when this device's view decides the size.</summary>
    public bool OwnsSize { get; set; }
}

public sealed class ChannelMessage
{
    public uint Channel { get; set; }
}

public sealed class ResizeMessage
{
    public uint Channel { get; set; }
    public int Columns { get; set; }
    public int Rows { get; set; }
    /// <summary>Take the say over the size for this device's view (the person there is using the terminal now).</summary>
    public bool Claim { get; set; }
}

public sealed class ResizedMessage
{
    /// <summary>False when another view decides the size: the terminal keeps <see cref="Columns"/> by <see cref="Rows"/>.</summary>
    public bool Applied { get; set; }
    public int Columns { get; set; }
    public int Rows { get; set; }
}

public sealed class ExitedMessage
{
    public uint Channel { get; set; }
    public int? ExitCode { get; set; }
}

public sealed class PathMessage
{
    public string Path { get; set; } = "";
}

public sealed class ResolvedMessage
{
    /// <summary>The absolute path on the host.</summary>
    public string Path { get; set; } = "";
}

public sealed class ExistsMessage
{
    public bool Exists { get; set; }
}

public sealed class FileEntryMessage
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsHidden { get; set; }
}

public sealed class FileListMessage
{
    public List<FileEntryMessage> Entries { get; set; } = [];
}

public sealed class ReadFileMessage
{
    public string Path { get; set; } = "";
    public long Offset { get; set; }
    /// <summary>At most <see cref="RemoteMethods.FilePieceBytes"/>.</summary>
    public int Length { get; set; }
}

public sealed class FilePieceMessage
{
    /// <summary>The file's whole size.</summary>
    public long Size { get; set; }
    /// <summary>The bytes from the asked offset, base64.</summary>
    public string Data { get; set; } = "";
}

public sealed class WriteFileMessage
{
    public string Path { get; set; } = "";
    /// <summary>One id for all pieces of one write: they collect beside the file and replace it with the last piece.</summary>
    public string Upload { get; set; } = "";
    public string Data { get; set; } = "";
    public bool Last { get; set; }
}

/// <summary>One piece of a picture for a session (<see cref="RemoteMethods.FsUpload"/>).</summary>
public sealed class UploadMessage
{
    /// <summary>The session the picture is for.</summary>
    public string ItemId { get; set; } = "";
    /// <summary>One id for all pieces of one picture, letters and digits.</summary>
    public string Upload { get; set; } = "";
    /// <summary>The picture's file name on the device: only its extension is kept (png, jpg, jpeg, gif or webp).</summary>
    public string Name { get; set; } = "";
    /// <summary>At most <see cref="RemoteMethods.FilePieceBytes"/>, base64.</summary>
    public string Data { get; set; } = "";
    public bool Last { get; set; }
}

public sealed class UploadedMessage
{
    /// <summary>Where the host keeps the picture: given with the last piece only.</summary>
    public string? Path { get; set; }
}

public sealed class MoveMessage
{
    public string Path { get; set; } = "";
    public string NewPath { get; set; } = "";
}

public sealed class DeleteMessage
{
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
}

public sealed class BrowseFolderMessage
{
    /// <summary>The folder to list; null or empty means the host's home folder (unless <see cref="ListRoots"/> is true).</summary>
    public string? Path { get; set; }
    /// <summary>True to list system root entries (drive letters on Windows, "/" on Unix).</summary>
    public bool ListRoots { get; set; }
    /// <summary>When true and <see cref="Path"/> does not exist, walk up to the nearest existing ancestor (or home).</summary>
    public bool FallbackToParent { get; set; }
}

public sealed class BrowseEntryMessage
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsHidden { get; set; }
    public bool IsExistingProject { get; set; }
    /// <summary><c>folder</c>, <c>drive</c>, or <c>home</c>.</summary>
    public string Kind { get; set; } = "folder";
}

public sealed class BrowseFolderResultMessage
{
    /// <summary>Normalized current folder path; empty string stands for a virtual root (Windows drive list).</summary>
    public string Path { get; set; } = "";
    /// <summary>Parent folder path; empty string to step up to the Windows drive list; null when already at the top.</summary>
    public string? Parent { get; set; }
    /// <summary>Path separator on the host (<c>"/"</c> or <c>"\\"</c>).</summary>
    public string Separator { get; set; } = "/";
    /// <summary>The host's home folder.</summary>
    public string Home { get; set; } = "";
    public List<BrowseEntryMessage> Roots { get; set; } = [];
    public List<BrowseEntryMessage> Entries { get; set; } = [];
}

public sealed class CreateBrowseFolderMessage
{
    public string ParentPath { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class CallMessage
{
    public string Method { get; set; } = "";
    /// <summary>The arguments, as JSON text the two apps agree on (this library does not know the types).</summary>
    public string Arguments { get; set; } = "";
}

public sealed class CallResultMessage
{
    public string Result { get; set; } = "";
}

/// <summary>Asks the host to stream a file's raw bytes over <see cref="Channel"/> (v2, <see cref="RemoteMethods.FsReadStream"/>).</summary>
public sealed class ReadStreamMessage
{
    public string Path { get; set; } = "";
    public uint Channel { get; set; }
    public long MaxBytes { get; set; }
}

/// <summary>The host's answer to <see cref="RemoteMethods.FsReadStream"/> before <see cref="FrameType.StreamData"/> frames arrive.</summary>
public sealed class ReadStreamReadyMessage
{
    public long Size { get; set; }
}

/// <summary>Opens a binary receiver on <see cref="Channel"/> for writing a file on the host (v2, <see cref="RemoteMethods.FsWriteStream"/>).</summary>
public sealed class WriteStreamMessage
{
    public string Path { get; set; } = "";
    public uint Channel { get; set; }
    public long Size { get; set; }
}

/// <summary>Opens a binary receiver on <see cref="Channel"/> for uploading a session picture on the host (v2, <see cref="RemoteMethods.FsUploadStream"/>).</summary>
public sealed class UploadStreamMessage
{
    public string ItemId { get; set; } = "";
    public string Name { get; set; } = "";
    public uint Channel { get; set; }
    public long Size { get; set; }
}

/// <summary>One ICE/UDP candidate for WebRTC P2P direct transport hole punching (v2).</summary>
public sealed class P2pCandidateMessage
{
    public string Foundation { get; set; } = "1";
    public int Component { get; set; } = 1;
    public string Transport { get; set; } = "udp";
    public uint Priority { get; set; }
    public string Address { get; set; } = "";
    public int Port { get; set; }
    /// <summary><c>host</c> or <c>srflx</c>.</summary>
    public string Type { get; set; } = "host";
}

/// <summary>Credentials and UDP candidates exchanged over an existing connection to establish a WebRTC P2P direct channel (v2).</summary>
public sealed class P2pHandshakeMessage
{
    public string Ufrag { get; set; } = "";
    public string Pwd { get; set; } = "";
    public List<P2pCandidateMessage> Candidates { get; set; } = [];
}
