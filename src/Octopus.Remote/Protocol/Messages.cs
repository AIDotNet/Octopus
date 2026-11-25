using System.Text.Json.Serialization;

namespace Octopus.Remote.Protocol;

/// <summary>The version of the messages inside the secure channel, and the error codes both sides use.</summary>
public static class RemoteProtocol
{
    /// <summary>Changes only when an old peer could no longer be talked to. Everything else is added as capabilities.</summary>
    public const int Version = 1;

    public const string Hello = "hello";

    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    /// <summary>The two sides speak different protocol versions; the message says which one has to be updated.</summary>
    public const int VersionMismatch = -33003;
}

/// <summary>The other side answered a request with an error.</summary>
public sealed class RemoteException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>What each side tells the other first: its protocol version, its app, and what it can do.</summary>
public sealed class HelloMessage
{
    public int Protocol { get; set; } = RemoteProtocol.Version;
    public string AppVersion { get; set; } = "";
    /// <summary>
    /// Features this side has, by name. A feature is used only when both sides list it, so a newer app talks to an
    /// older one without either knowing the other's version.
    /// </summary>
    public List<string> Capabilities { get; set; } = [];
    /// <summary>
    /// The build of the terminal library. Two sides with the same one hand over terminals as the library's own
    /// snapshot; otherwise as VT text (design/12-远程操作.md §4.3).
    /// </summary>
    public string? TerminalBuild { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(Nothing))]
[JsonSerializable(typeof(HostInfoMessage))]
[JsonSerializable(typeof(ProjectListMessage))]
[JsonSerializable(typeof(ProjectMessage))]
[JsonSerializable(typeof(WorkspaceMessage))]
[JsonSerializable(typeof(WorkspaceBranchMessage))]
[JsonSerializable(typeof(WorkspaceTaskOptionMessage))]
[JsonSerializable(typeof(WorkspaceOptionsMessage))]
[JsonSerializable(typeof(CreateWorkspaceMessage))]
[JsonSerializable(typeof(CreateProjectMessage))]
[JsonSerializable(typeof(ProjectRefMessage))]
[JsonSerializable(typeof(ItemListMessage))]
[JsonSerializable(typeof(ItemMessage))]
[JsonSerializable(typeof(CreateItemMessage))]
[JsonSerializable(typeof(ItemRefMessage))]
[JsonSerializable(typeof(RenameItemMessage))]
[JsonSerializable(typeof(AgentListMessage))]
[JsonSerializable(typeof(AttachMessage))]
[JsonSerializable(typeof(AttachedMessage))]
[JsonSerializable(typeof(ChannelMessage))]
[JsonSerializable(typeof(ResizeMessage))]
[JsonSerializable(typeof(ResizedMessage))]
[JsonSerializable(typeof(ExitedMessage))]
[JsonSerializable(typeof(PathMessage))]
[JsonSerializable(typeof(FileListMessage))]
[JsonSerializable(typeof(ResolvedMessage))]
[JsonSerializable(typeof(ExistsMessage))]
[JsonSerializable(typeof(ReadFileMessage))]
[JsonSerializable(typeof(FilePieceMessage))]
[JsonSerializable(typeof(WriteFileMessage))]
[JsonSerializable(typeof(UploadMessage))]
[JsonSerializable(typeof(UploadedMessage))]
[JsonSerializable(typeof(MoveMessage))]
[JsonSerializable(typeof(DeleteMessage))]
[JsonSerializable(typeof(BrowseFolderMessage))]
[JsonSerializable(typeof(BrowseFolderResultMessage))]
[JsonSerializable(typeof(BrowseEntryMessage))]
[JsonSerializable(typeof(CreateBrowseFolderMessage))]
[JsonSerializable(typeof(CallMessage))]
[JsonSerializable(typeof(CallResultMessage))]
[JsonSerializable(typeof(AcpRefMessage))]
[JsonSerializable(typeof(AcpSnapshotMessage))]
[JsonSerializable(typeof(AcpUpdateMessage))]
[JsonSerializable(typeof(AcpEntryMessage))]
[JsonSerializable(typeof(AcpToolMessage))]
[JsonSerializable(typeof(AcpWorkNoteMessage))]
[JsonSerializable(typeof(AcpPlanEntryMessage))]
[JsonSerializable(typeof(AcpPermissionMessage))]
[JsonSerializable(typeof(AcpOptionMessage))]
[JsonSerializable(typeof(AcpQuestionMessage))]
[JsonSerializable(typeof(AcpQuestionFieldMessage))]
[JsonSerializable(typeof(AcpComposerMessage))]
[JsonSerializable(typeof(AcpPromptMessage))]
[JsonSerializable(typeof(AcpRespondMessage))]
[JsonSerializable(typeof(AcpSetOptionMessage))]
public sealed partial class RemoteJsonContext : JsonSerializerContext;
