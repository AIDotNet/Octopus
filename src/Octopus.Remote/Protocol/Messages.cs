using System.Text.Json.Serialization;

namespace Octopus.Remote.Protocol;

/// <summary>The version of the messages inside the secure channel, and the error codes both sides use.</summary>
public static class RemoteProtocol
{
    /// <summary>Base wire version kept in <see cref="HelloMessage.Protocol"/> so v1 peers never reject a v2 hello.</summary>
    public const int Version = 1;
    /// <summary>The latest protocol version this build supports via <see cref="HelloMessage.MaxProtocol"/> and <see cref="TransportNegotiation"/>.</summary>
    public const int LatestVersion = 2;

    public const string Hello = "hello";

    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    /// <summary>The two sides speak different protocol versions; the message says which one has to be updated.</summary>
    public const int VersionMismatch = -33003;
}

/// <summary>Names of payload compression algorithms negotiated in <see cref="TransportNegotiation.Compression"/>.</summary>
public static class CompressionAlgorithms
{
    public const string Brotli = "brotli";
    public const string Deflate = "deflate";
}

/// <summary>Names of transport mechanisms advertised in <see cref="TransportOffer.Kind"/>.</summary>
public static class TransportKinds
{
    public const string WebSocket = "ws";
    public const string Relay = "relay";
    public const string Quic = "quic";
    public const string WebRtcP2p = "p2p";
}

/// <summary>The other side answered a request with an error.</summary>
public sealed class RemoteException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>An alternative or upgrade transport endpoint a host offers (e.g., QUIC or WebRTC P2P).</summary>
public sealed class TransportOffer
{
    public string Kind { get; set; } = "";
    public string Address { get; set; } = "";
    public int Priority { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
}

/// <summary>
/// Frame-level and transport-level negotiation exchanged inside <see cref="HelloMessage.Transport"/> (v2).
/// Older v1 peers ignore this field and leave it null in their answer.
/// </summary>
public sealed class TransportNegotiation
{
    /// <summary>The frame protocol version supported or selected (1 = legacy, 2 = compression + priority + binary streams + ping/pong).</summary>
    public int FrameVersion { get; set; } = RemoteProtocol.LatestVersion;
    /// <summary>Supported compression algorithms in preference order; the host's answer lists the single chosen algorithm.</summary>
    public List<string>? Compression { get; set; }
    /// <summary>Minimum payload size in bytes before compression is attempted.</summary>
    public int? CompressThreshold { get; set; }
    /// <summary>Preferred maximum chunk size for snapshots and binary streams (default 64 KiB).</summary>
    public int? MaxChunkBytes { get; set; }
    /// <summary>Transport endpoints the host offers for direct connection or live transport migration.</summary>
    public List<TransportOffer>? Transports { get; set; }

    public static TransportNegotiation CreateDefault(List<TransportOffer>? transports = null) => new()
    {
        FrameVersion = RemoteProtocol.LatestVersion,
        Compression = [CompressionAlgorithms.Brotli, CompressionAlgorithms.Deflate],
        CompressThreshold = FrameCodec.DefaultThreshold,
        MaxChunkBytes = 64 * 1024,
        Transports = transports,
    };
}

/// <summary>What each side tells the other first: its protocol version, its app, and what it can do.</summary>
public sealed class HelloMessage
{
    public int Protocol { get; set; } = RemoteProtocol.Version;
    /// <summary>Lowest protocol version this peer supports; null means <see cref="Protocol"/>.</summary>
    public int? MinProtocol { get; set; }
    /// <summary>Highest protocol version this peer supports; null means <see cref="Protocol"/> (or <see cref="TransportNegotiation.FrameVersion"/>).</summary>
    public int? MaxProtocol { get; set; }
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
    /// <summary>Protocol v2 frame and transport negotiation parameters; null from a v1 peer.</summary>
    public TransportNegotiation? Transport { get; set; }
    /// <summary>Inlined host information from a v2 host, saving a separate <c>host/info</c> round trip on connect.</summary>
    public HostInfoMessage? HostInfo { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(TransportNegotiation))]
[JsonSerializable(typeof(TransportOffer))]
[JsonSerializable(typeof(Nothing))]
[JsonSerializable(typeof(HostInfoMessage))]
[JsonSerializable(typeof(ProjectListMessage))]
[JsonSerializable(typeof(ProjectMessage))]
[JsonSerializable(typeof(ProjectGroupMessage))]
[JsonSerializable(typeof(CreateProjectGroupMessage))]
[JsonSerializable(typeof(UpdateProjectGroupMessage))]
[JsonSerializable(typeof(ProjectGroupRefMessage))]
[JsonSerializable(typeof(SetProjectGroupMessage))]
[JsonSerializable(typeof(WorkspaceMessage))]
[JsonSerializable(typeof(WorkspaceBranchMessage))]
[JsonSerializable(typeof(WorkspaceTaskOptionMessage))]
[JsonSerializable(typeof(WorkspaceOptionsMessage))]
[JsonSerializable(typeof(CreateWorkspaceMessage))]
[JsonSerializable(typeof(CreateProjectMessage))]
[JsonSerializable(typeof(ProjectRefMessage))]
[JsonSerializable(typeof(ProjectIconMessage))]
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
[JsonSerializable(typeof(ReadStreamMessage))]
[JsonSerializable(typeof(ReadStreamReadyMessage))]
[JsonSerializable(typeof(WriteStreamMessage))]
[JsonSerializable(typeof(UploadStreamMessage))]
[JsonSerializable(typeof(P2pCandidateMessage))]
[JsonSerializable(typeof(P2pHandshakeMessage))]
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
[JsonSerializable(typeof(AcpWorkflowTaskMessage))]
[JsonSerializable(typeof(List<AcpWorkflowTaskMessage>))]
[JsonSerializable(typeof(AcpPermissionMessage))]
[JsonSerializable(typeof(AcpOptionMessage))]
[JsonSerializable(typeof(AcpQuestionMessage))]
[JsonSerializable(typeof(AcpQuestionFieldMessage))]
[JsonSerializable(typeof(AcpComposerMessage))]
[JsonSerializable(typeof(AcpPromptMessage))]
[JsonSerializable(typeof(AcpMentionMessage))]
[JsonSerializable(typeof(AcpMentionTargetsRequestMessage))]
[JsonSerializable(typeof(AcpMentionTargetsMessage))]
[JsonSerializable(typeof(AcpMentionTargetMessage))]
[JsonSerializable(typeof(AcpRespondMessage))]
[JsonSerializable(typeof(AcpSetOptionMessage))]
[JsonSerializable(typeof(SystemMetricsRequestMessage))]
[JsonSerializable(typeof(SystemHostMessage))]
[JsonSerializable(typeof(CpuMetricsMessage))]
[JsonSerializable(typeof(MemoryMetricsMessage))]
[JsonSerializable(typeof(DiskMetricsMessage))]
[JsonSerializable(typeof(NetworkInterfaceMessage))]
[JsonSerializable(typeof(ProcessMetricsMessage))]
[JsonSerializable(typeof(SystemMetricsSnapshotMessage))]
[JsonSerializable(typeof(SignalProcessMessage))]
[JsonSerializable(typeof(SignalProcessResultMessage))]
[JsonSerializable(typeof(RemoteProcessRunMessage))]
[JsonSerializable(typeof(RemoteProcessRunResultMessage))]
[JsonSerializable(typeof(BotMessage))]
[JsonSerializable(typeof(BotListMessage))]
[JsonSerializable(typeof(BotChatMessage))]
[JsonSerializable(typeof(BotConnectorMessage))]
[JsonSerializable(typeof(BotWorkItemMessage))]
[JsonSerializable(typeof(CreateBotMessage))]
[JsonSerializable(typeof(ImportBotMessage))]
[JsonSerializable(typeof(BotRefMessage))]
[JsonSerializable(typeof(UpdateBotMessage))]
[JsonSerializable(typeof(CreateBotChatMessage))]
[JsonSerializable(typeof(BotChatRefMessage))]
[JsonSerializable(typeof(RenameBotChatMessage))]
[JsonSerializable(typeof(ResolveBotItemMessage))]
[JsonSerializable(typeof(ToggleBotConnectorMessage))]
[JsonSerializable(typeof(BotGroupMessageDto))]
[JsonSerializable(typeof(BotGroupMemberDto))]
[JsonSerializable(typeof(BotGroupThreadDto))]
[JsonSerializable(typeof(BotGroupListMessage))]
[JsonSerializable(typeof(BotGroupTurnDto))]
[JsonSerializable(typeof(BotGroupEntryDto))]
[JsonSerializable(typeof(BotGroupApprovalDto))]
[JsonSerializable(typeof(BotGroupRoomReadRequestMessage))]
[JsonSerializable(typeof(BotGroupRoomSnapshotMessage))]
[JsonSerializable(typeof(CreateBotGroupMessage))]
[JsonSerializable(typeof(UpdateBotGroupMessage))]
[JsonSerializable(typeof(BotGroupRefMessage))]
[JsonSerializable(typeof(BotGroupSendMessage))]
[JsonSerializable(typeof(BotGroupStopMessage))]
[JsonSerializable(typeof(BotGroupContinueHandoffMessage))]
[JsonSerializable(typeof(BotGroupApprovalRespondMessage))]
[JsonSerializable(typeof(LlmTunnelProviderMessage))]
[JsonSerializable(typeof(LlmTunnelModelMessage))]
[JsonSerializable(typeof(LlmProxyOpenMessage))]
[JsonSerializable(typeof(LlmProxyOpenResultMessage))]
[JsonSerializable(typeof(LlmProxyCancelMessage))]
[JsonSerializable(typeof(LlmProxyRebindMessage))]
[JsonSerializable(typeof(LlmProxyRevokeMessage))]
public sealed partial class RemoteJsonContext : JsonSerializerContext;
