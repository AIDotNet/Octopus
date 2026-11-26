using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Acp.Client;
using Octopus.Acp.Dialects;
using Octopus.Acp.Transport;
using Octopus.Core.Agents;

namespace Octopus.Acp.Protocol;

/// <summary>Event emitted by <see cref="AcpSessionRuntime"/> as the agent streams updates or requests input.</summary>
public abstract record AcpRuntimeEvent
{
    /// <summary>True when this content event replays prior conversation history (session/load or <c>_meta.isReplay</c>).</summary>
    public bool IsReplay { get; init; }
    /// <summary>ACP session id attached to the notification envelope, when present.</summary>
    public string? SessionId { get; init; }
}

/// <summary>A user prompt chunk; only emitted for replayed history (live prompts are rendered by the client itself).</summary>
public sealed record AcpUserMessageEvent(string Text, string? MessageId) : AcpRuntimeEvent;
public sealed record AcpUserImageEvent(AcpImageContent Image, string? MessageId) : AcpRuntimeEvent;
public sealed record AcpTextDeltaEvent(string Channel, string Text, string? MessageId) : AcpRuntimeEvent;
public sealed record AcpImageEvent(AcpImageContent Image, string? MessageId) : AcpRuntimeEvent;
public sealed record AcpAudioEvent(AcpAudioContent Audio, string? MessageId) : AcpRuntimeEvent;
public sealed record AcpToolCallEvent(AcpToolCallUpdate Update) : AcpRuntimeEvent;
public sealed record AcpPlanEvent(IReadOnlyList<AcpPlanEntry> Entries, string? Markdown = null) : AcpRuntimeEvent;
public sealed record AcpPlanRemovedEvent() : AcpRuntimeEvent;
public sealed record AcpUsageEvent(AcpContextUsage Usage) : AcpRuntimeEvent;
public sealed record AcpCommandsUpdatedEvent(IReadOnlyList<AcpAvailableCommand> Commands) : AcpRuntimeEvent;
public sealed record AcpModesUpdatedEvent(AcpSessionModeState Modes) : AcpRuntimeEvent;
public sealed record AcpConfigOptionsUpdatedEvent(IReadOnlyList<AcpSessionConfigOption> Options) : AcpRuntimeEvent;
public sealed record AcpModelsUpdatedEvent(AcpSessionModelState Models) : AcpRuntimeEvent;
public sealed record AcpTitleUpdatedEvent(string Title) : AcpRuntimeEvent;
public sealed record AcpSessionInfoUpdatedEvent(AcpSessionInfoUpdate Info) : AcpRuntimeEvent;
public sealed record AcpElicitationCompletedEvent(string ElicitationId) : AcpRuntimeEvent;
public sealed record AcpCompactionEvent(AcpCompactionUpdate Update) : AcpRuntimeEvent;
public sealed record AcpCursorUpdatedEvent(AcpCursorUpdate Cursor) : AcpRuntimeEvent;
public sealed record AcpDiagnosticEvent(string Message) : AcpRuntimeEvent;
public sealed record AcpClosedEvent(Exception Reason) : AcpRuntimeEvent;

/// <summary>Options controlling an <see cref="AcpSessionRuntime"/> instance.</summary>
public sealed class AcpSessionRuntimeOptions
{
    public IAcpDialect Dialect { get; init; } = GenericAcpDialect.Instance;
    public AcpPermissionStrategy PermissionStrategy { get; init; } = AcpPermissionStrategy.ClientApproval;
    public int PreferredProtocolVersion { get; init; } = AcpProtocolConstants.ProtocolVersion;
    public bool EnableFormElicitation { get; init; } = true;
    public bool EnableUrlElicitation { get; init; } = true;
    public bool EnableClientFileSystem { get; init; }
    public bool EnableClientTerminal { get; init; }
    public bool EnableTerminalAuth { get; init; } = true;
    public string? AgentVersion { get; init; }
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<AcpMcpServerConfig> McpServers { get; init; } = [];
    public IAcpFileSystemHandler? FileSystemHandler { get; init; }
    public IAcpTerminalHandler? TerminalHandler { get; init; }
    public Func<string>? GetActivePermissionMode { get; init; }
    public Func<AcpPermissionRequest, CancellationToken, Task<string?>>? OnPermissionRequest { get; init; }
    public Func<AcpQuestionRequest, CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?>>? OnQuestionRequest { get; init; }
    public Func<AcpQuestionRequest, CancellationToken, Task<AcpFormElicitationResponse>>? OnFormElicitationRequest { get; init; }
    public Func<AcpUrlElicitationRequest, CancellationToken, Task<AcpElicitationAction>>? OnUrlElicitationRequest { get; init; }
}

/// <summary>
/// Full ACP v1/v2-preview session runtime over a bidirectional <see cref="AcpJsonRpcPeer"/>.
/// Handles initialize, authenticate/logout, session/new|load|resume|list|fork|close|delete|archive,
/// prompt streaming, live mode/model/config changes, client fs/* and terminal/* callbacks,
/// and safe permission + form/URL elicitation callbacks.
/// </summary>
public sealed class AcpSessionRuntime : IAsyncDisposable
{
    private readonly AcpJsonRpcPeer _peer;
    private readonly AcpSessionRuntimeOptions _options;
    private AcpInitializeResult? _initialized;
    private AcpSessionStartResult? _started;
    private AcpSessionModeState? _modes;
    private List<AcpSessionConfigOption> _configOptions = [];
    private AcpSessionModelState? _models;
    private List<AcpAvailableCommand> _commands = [];
    private volatile bool _isLoadingHistory;

    public AcpSessionRuntime(Stream input, Stream output, AcpSessionRuntimeOptions? options = null)
        : this(new AcpStreamTransport(input, output), options)
    {
    }

    public AcpSessionRuntime(IAcpTransport transport, AcpSessionRuntimeOptions? options = null)
    {
        _options = options ?? new AcpSessionRuntimeOptions();
        _peer = new AcpJsonRpcPeer(transport, new AcpPeerHandlers
        {
            OnRequest = HandleIncomingRequestAsync,
            OnNotification = HandleNotification,
            // Close the replay window in read order so updates after the session/load reply are treated as live.
            OnResponse = method => { if (method == "session/load") _isLoadingHistory = false; },
            OnOtherLine = line => Emit(new AcpDiagnosticEvent(line)),
            OnDiagnostic = msg => Emit(new AcpDiagnosticEvent(msg)),
            OnClose = err => Emit(new AcpClosedEvent(err)),
        });
    }

    public event Action<AcpRuntimeEvent>? EventReceived;

    public bool IsClosed => _peer.IsClosed;
    public AcpInitializeResult? Initialized => _initialized;
    public string? SessionId => _started?.SessionId;
    public AcpSessionModeState? Modes => _modes;
    public IReadOnlyList<AcpSessionConfigOption> ConfigOptions => _configOptions;
    public AcpSessionModelState? Models => _models;
    public IReadOnlyList<AcpAvailableCommand> AvailableCommands => _commands;
    public IAcpDialect Dialect => _options.Dialect;

    public async Task<AcpInitializeResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized is not null) return _initialized;

        var fsEnabled = _options.EnableClientFileSystem && _options.FileSystemHandler is not null;
        var termEnabled = _options.EnableClientTerminal && _options.TerminalHandler is not null;

        var clientCaps = new JsonObject
        {
            ["fs"] = new JsonObject { ["readTextFile"] = fsEnabled, ["writeTextFile"] = fsEnabled },
            ["terminal"] = termEnabled,
        };

        var elicitCaps = new JsonObject();
        if (_options.EnableFormElicitation && _options.Dialect.AdvertiseFormElicitation)
        {
            elicitCaps["form"] = new JsonObject();
        }
        if (_options.EnableUrlElicitation)
        {
            elicitCaps["url"] = new JsonObject();
        }
        if (elicitCaps.Count > 0)
        {
            clientCaps["elicitation"] = elicitCaps;
        }
        if (_options.EnableTerminalAuth)
        {
            clientCaps["auth"] = new JsonObject { ["terminal"] = true };
        }
        if (_options.Dialect.ExtraClientCapabilitiesMeta is { Count: > 0 } extraMeta)
        {
            var metaObj = new JsonObject();
            foreach (var (k, v) in extraMeta)
            {
                metaObj[k] = v.DeepClone();
            }
            clientCaps["_meta"] = metaObj;
        }

        var clientInfo = _options.Dialect.OverrideClientInfo(_options.AgentVersion)
            ?? new AcpImplementation { Name = "octopus", Title = "Octopus", Version = "1.0.0" };

        var requestedVersion = Math.Clamp(
            _options.PreferredProtocolVersion,
            AcpProtocolConstants.MinSupportedVersion,
            AcpProtocolConstants.MaxSupportedVersion);

        var req = new JsonObject
        {
            ["protocolVersion"] = requestedVersion,
            ["clientCapabilities"] = clientCaps,
            ["clientInfo"] = new JsonObject
            {
                ["name"] = clientInfo.Name,
                ["title"] = clientInfo.Title ?? clientInfo.Name,
                ["version"] = clientInfo.Version,
            },
        };

        var raw = await _peer.RequestAsync("initialize", req, _options.InitializeTimeout, cancellationToken).ConfigureAwait(false);
        var parsed = AcpProtocolParser.ParseInitialize(raw);
        if (parsed.ProtocolVersion < AcpProtocolConstants.MinSupportedVersion
            || parsed.ProtocolVersion > AcpProtocolConstants.MaxSupportedVersion)
        {
            var err = new AcpRpcException(-32602, $"Unsupported ACP protocol version: {parsed.ProtocolVersion}");
            _peer.Close(err);
            throw err;
        }
        _initialized = parsed;
        return parsed;
    }

    public async Task<AcpSessionStartResult> StartSessionAsync(
        string workingDirectory,
        IReadOnlyList<string>? additionalDirectories = null,
        string? resumeSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var init = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var mcpServers = AcpProtocolParser.SerializeMcpServers(_options.McpServers, init.AgentCapabilities.McpCapabilities);

        async Task<AcpSessionStartResult> OpenAsync()
        {
            var baseParams = new JsonObject
            {
                ["cwd"] = workingDirectory,
                ["mcpServers"] = mcpServers.DeepClone(),
            };
            if (additionalDirectories is { Count: > 0 } && init.AgentCapabilities.SessionCapabilities.SupportsAdditionalDirectories)
            {
                var dirs = new JsonArray();
                foreach (var d in additionalDirectories) dirs.Add((JsonNode)JsonValue.Create(d));
                baseParams["additionalDirectories"] = dirs;
            }

            if (!string.IsNullOrWhiteSpace(resumeSessionId))
            {
                var tryResumeAfterLoad = true;
                if (init.AgentCapabilities.LoadSession)
                {
                    var loadParams = (JsonObject)baseParams.DeepClone();
                    loadParams["sessionId"] = resumeSessionId;
                    _isLoadingHistory = true;
                    try
                    {
                        var loaded = await _peer.RequestAsync("session/load", loadParams, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                        return AcpProtocolParser.ParseSessionStart("load", resumeSessionId, loaded);
                    }
                    catch (AcpRpcException ex) when (!_options.Dialect.IsAuthenticationRequired(ex))
                    {
                        Emit(new AcpDiagnosticEvent($"session/load failed for '{resumeSessionId}' ({ex.Message}); falling back."));
                        // Only try session/resume when the agent rejected the method itself; a missing/stale session id
                        // would fail session/resume as well and should start fresh via session/new.
                        tryResumeAfterLoad = ex.Code == -32601;
                    }
                    finally { _isLoadingHistory = false; }
                }
                if (tryResumeAfterLoad && init.AgentCapabilities.SessionCapabilities.SupportsResume)
                {
                    var resumeParams = (JsonObject)baseParams.DeepClone();
                    resumeParams["sessionId"] = resumeSessionId;
                    try
                    {
                        var resumed = await _peer.RequestAsync("session/resume", resumeParams, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                        return AcpProtocolParser.ParseSessionStart("resume", resumeSessionId, resumed);
                    }
                    catch (AcpRpcException ex) when (!_options.Dialect.IsAuthenticationRequired(ex))
                    {
                        Emit(new AcpDiagnosticEvent($"session/resume failed for '{resumeSessionId}' ({ex.Message}); falling back to session/new."));
                    }
                }
            }

            var created = await _peer.RequestAsync("session/new", baseParams, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            return AcpProtocolParser.ParseSessionStart("new", null, created);
        }

        AcpSessionStartResult started;
        try
        {
            started = await OpenAsync().ConfigureAwait(false);
        }
        catch (AcpRpcException ex) when (_options.Dialect.IsAuthenticationRequired(ex))
        {
            var authId = _options.Dialect.SelectAuthMethod(init.AuthMethods, _options.Environment);
            if (string.IsNullOrEmpty(authId))
            {
                throw new AcpAuthRequiredException(ex.Message, init.AuthMethods, ex.ErrorData);
            }
            await AuthenticateAsync(authId, meta: null, cancellationToken).ConfigureAwait(false);
            started = await OpenAsync().ConfigureAwait(false);
        }

        ApplyStartedState(started);
        return started;
    }

    public async Task AuthenticateAsync(
        string methodId,
        IReadOnlyDictionary<string, string>? meta = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var req = new JsonObject { ["methodId"] = methodId };
        if (meta is { Count: > 0 })
        {
            var metaObj = new JsonObject();
            foreach (var (k, v) in meta) metaObj[k] = v;
            req["_meta"] = metaObj;
        }
        await _peer.RequestAsync("authenticate", req, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var init = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!init.AgentCapabilities.AuthCapabilities.Logout)
        {
            throw new AcpRpcException(-32601, "ACP agent does not advertise auth.logout capability.");
        }
        await _peer.RequestAsync("auth/logout", new JsonObject(), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AcpSessionListResult> ListSessionsAsync(
        string? workingDirectory = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var init = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!init.AgentCapabilities.SessionCapabilities.SupportsList)
        {
            throw new AcpRpcException(-32601, "ACP agent does not advertise sessionCapabilities.list.");
        }
        var req = new JsonObject();
        if (!string.IsNullOrWhiteSpace(workingDirectory)) req["cwd"] = workingDirectory;
        if (!string.IsNullOrWhiteSpace(cursor)) req["cursor"] = cursor;
        var raw = await _peer.RequestAsync("session/list", req, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return AcpProtocolParser.ParseSessionListResult(raw);
    }

    public async Task<AcpSessionStartResult> ForkSessionAsync(
        string? sessionId = null,
        string? workingDirectory = null,
        string? cursorId = null,
        CancellationToken cancellationToken = default)
    {
        var init = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!init.AgentCapabilities.SessionCapabilities.SupportsFork)
        {
            throw new AcpRpcException(-32601, "ACP agent does not advertise sessionCapabilities.fork.");
        }
        var sourceId = string.IsNullOrWhiteSpace(sessionId) ? RequireSessionId() : sessionId;
        var req = new JsonObject
        {
            ["sessionId"] = sourceId,
            ["mcpServers"] = AcpProtocolParser.SerializeMcpServers(_options.McpServers, init.AgentCapabilities.McpCapabilities),
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) req["cwd"] = workingDirectory;
        if (!string.IsNullOrWhiteSpace(cursorId)) req["cursorId"] = cursorId;

        var raw = await _peer.RequestAsync("session/fork", req, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return AcpProtocolParser.ParseSessionStart("fork", null, raw);
    }

    public async Task CloseSessionAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrWhiteSpace(sessionId) ? _started?.SessionId : sessionId;
        if (string.IsNullOrWhiteSpace(id)) return;
        if (_initialized?.AgentCapabilities.SessionCapabilities.SupportsClose != true) return;

        await _peer.RequestAsync(
            "session/close",
            new JsonObject { ["sessionId"] = id },
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var init = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!init.AgentCapabilities.SessionCapabilities.SupportsDelete)
        {
            throw new AcpRpcException(-32601, "ACP agent does not advertise sessionCapabilities.delete.");
        }
        try
        {
            await _peer.RequestAsync(
                "session/delete",
                new JsonObject { ["sessionId"] = sessionId },
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcpRpcException ex) when (ex.Code == -32002)
        {
            // Per ACP spec, -32002 (resource not found) on session/delete is idempotent success.
        }
    }

    public async Task ArchiveSessionAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var init = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!init.AgentCapabilities.SessionCapabilities.SupportsArchive)
        {
            throw new AcpRpcException(-32601, "ACP agent does not advertise sessionCapabilities.archive.");
        }
        var id = string.IsNullOrWhiteSpace(sessionId) ? RequireSessionId() : sessionId;
        await _peer.RequestAsync(
            "session/archive",
            new JsonObject { ["sessionId"] = id },
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AcpPromptResult> PromptAsync(
        string text,
        IReadOnlyList<AcpPromptAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = RequireSessionId();
        var effectiveText = RewritePromptCommand(text);
        var promptCaps = _initialized?.AgentCapabilities.PromptCapabilities;
        var imageSupported = promptCaps?.Image == true;
        var audioSupported = promptCaps?.Audio == true;
        var embeddedSupported = promptCaps?.EmbeddedContext == true;
        var blocks = AcpProtocolParser.BuildPromptBlocks(
            effectiveText,
            attachments,
            _options.Dialect,
            imageSupported,
            audioSupported,
            embeddedSupported);

        var req = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = blocks,
        };

        try
        {
            var raw = await _peer.RequestAsync("session/prompt", req, timeout: null, cancellationToken).ConfigureAwait(false);
            return AcpProtocolParser.ParsePromptResult(raw);
        }
        catch (AcpAuthRequiredException)
        {
            throw;
        }
        catch (AcpRpcException ex) when (_options.Dialect.IsAuthenticationRequired(ex))
        {
            throw new AcpAuthRequiredException(ex.Message, _initialized?.AuthMethods, ex.ErrorData);
        }
        catch (AcpRpcException ex)
        {
            if (_options.Dialect.FormatPromptError(ex) is { Length: > 0 } formatted
                && !string.Equals(formatted, ex.Message, StringComparison.Ordinal))
            {
                throw new AcpRpcException(ex.Code, formatted, ex.ErrorData);
            }
            throw;
        }
    }

    public Task CancelAsync()
    {
        if (_started is null) return Task.CompletedTask;
        _peer.CancelIncomingRequests();
        return _peer.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = _started.SessionId });
    }

    /// <summary>Rejects pending outgoing calls while still allowing trailing stdout notifications to drain before closing.</summary>
    public void DrainNotifications(Exception? error = null) => _peer.DrainNotifications(error);

    public async Task SetModeAsync(string modeId, CancellationToken cancellationToken = default)
    {
        var sessionId = RequireSessionId();
        // Prefer configOptions category == "mode" if present, else session/set_mode.
        var modeOpt = _configOptions.FirstOrDefault(c => c.Category == "mode" || c.Id == "mode");
        if (modeOpt is not null)
        {
            await SetConfigOptionAsync(modeOpt.Id, modeId, cancellationToken).ConfigureAwait(false);
            return;
        }
        await _peer.RequestAsync(
            "session/set_mode",
            new JsonObject { ["sessionId"] = sessionId, ["modeId"] = modeId },
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (_modes is not null)
        {
            _modes.CurrentModeId = modeId;
            Emit(new AcpModesUpdatedEvent(_modes));
        }
    }

    public async Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var sessionId = RequireSessionId();
        var mapped = _options.Dialect.ChatModelIdAliases?.GetValueOrDefault(modelId) ?? modelId;
        var modelOpt = _configOptions.FirstOrDefault(c => c.Category == "model" || c.Id == "model");
        if (modelOpt is not null)
        {
            await SetConfigOptionAsync(modelOpt.Id, mapped, cancellationToken).ConfigureAwait(false);
            return;
        }
        await _peer.RequestAsync(
            "session/set_model",
            new JsonObject { ["sessionId"] = sessionId, ["modelId"] = mapped },
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (_models is not null)
        {
            _models.CurrentModelId = mapped;
            Emit(new AcpModelsUpdatedEvent(_models));
        }
    }

    public async Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken = default)
    {
        var sessionId = RequireSessionId();
        var mapped = configId.Contains("effort", StringComparison.OrdinalIgnoreCase) || configId.Contains("thought", StringComparison.OrdinalIgnoreCase)
            ? (_options.Dialect.ChatEffortIdAliases?.GetValueOrDefault(value) ?? value)
            : value;
        var raw = await _peer.RequestAsync(
            "session/set_config_option",
            new JsonObject { ["sessionId"] = sessionId, ["configId"] = configId, ["value"] = mapped },
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (AcpProtocolParser.TryArr(raw, "configOptions") is { } arr)
        {
            _configOptions = AcpProtocolParser.ParseConfigOptions(arr);
            Emit(new AcpConfigOptionsUpdatedEvent(_configOptions));
        }
    }

    public async Task SetBooleanConfigOptionAsync(string configId, bool value, CancellationToken cancellationToken = default)
    {
        var sessionId = RequireSessionId();
        var raw = await _peer.RequestAsync(
            "session/set_config_option",
            new JsonObject { ["sessionId"] = sessionId, ["configId"] = configId, ["value"] = value, ["type"] = "boolean" },
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (AcpProtocolParser.TryArr(raw, "configOptions") is { } arr)
        {
            _configOptions = AcpProtocolParser.ParseConfigOptions(arr);
            Emit(new AcpConfigOptionsUpdatedEvent(_configOptions));
        }
    }

    public ValueTask DisposeAsync() => _peer.DisposeAsync();

    private void ApplyStartedState(AcpSessionStartResult started)
    {
        _started = started;
        if (started.Modes is not null)
        {
            _modes = started.Modes;
            Emit(new AcpModesUpdatedEvent(started.Modes) { SessionId = started.SessionId });
        }
        if (started.ConfigOptions.Count > 0)
        {
            _configOptions = started.ConfigOptions;
            Emit(new AcpConfigOptionsUpdatedEvent(_configOptions) { SessionId = started.SessionId });
        }
        if (started.Models is not null)
        {
            _models = started.Models;
            Emit(new AcpModelsUpdatedEvent(started.Models) { SessionId = started.SessionId });
        }
    }

    private string RequireSessionId() =>
        _started?.SessionId is { Length: > 0 } id ? id : throw new AcpException("ACP session has not started.");

    private string RewritePromptCommand(string text)
    {
        if (_options.Dialect.CompactCommand is { Length: > 0 } compact
            && text.TrimStart().StartsWith("/compact", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = text.TrimStart();
            var rest = trimmed.Length > 8 ? trimmed[8..] : "";
            return compact + rest;
        }
        return text;
    }

    private async Task<JsonNode?> HandleIncomingRequestAsync(string method, JsonElement? parameters, AcpRequestContext context)
    {
        // 1. Dialect custom request / override hook (questions, Cursor plan, Qwen queue drain, security warnings).
        var dialectHandled = _options.Dialect.TryHandleRequest(method, parameters, context.RawId);
        if (dialectHandled is not null)
        {
            if (dialectHandled.ImmediateReply is not null)
            {
                if (!string.IsNullOrWhiteSpace(dialectHandled.PlanMarkdown))
                {
                    Emit(new AcpPlanEvent([], dialectHandled.PlanMarkdown) { SessionId = _started?.SessionId });
                }
                if (dialectHandled.ToolCallUpdate is not null)
                {
                    Emit(new AcpToolCallEvent(dialectHandled.ToolCallUpdate) { SessionId = _started?.SessionId });
                }
                return dialectHandled.ImmediateReply;
            }
            if (dialectHandled.Question is not null && dialectHandled.BuildQuestionReply is not null)
            {
                IReadOnlyDictionary<string, IReadOnlyList<string>>? answers = null;
                if (_options.OnFormElicitationRequest is not null)
                {
                    var resp = await _options.OnFormElicitationRequest(dialectHandled.Question, context.CancellationToken).ConfigureAwait(false);
                    answers = resp.Action == AcpElicitationAction.Accept ? resp.Answers : null;
                }
                else if (_options.OnQuestionRequest is not null)
                {
                    answers = await _options.OnQuestionRequest(dialectHandled.Question, context.CancellationToken).ConfigureAwait(false);
                }
                return dialectHandled.BuildQuestionReply(answers);
            }
        }

        // 2. Standard ACP elicitation (form & url modes, never auto-approved).
        if (method == "elicitation/create")
        {
            if (AcpProtocolParser.ParseUrlElicitation(parameters, context.RawId) is { } urlReq)
            {
                if (!_options.EnableUrlElicitation || _options.OnUrlElicitationRequest is null)
                {
                    return AcpProtocolParser.BuildUrlElicitationReply(AcpElicitationAction.Decline);
                }
                var action = await _options.OnUrlElicitationRequest(urlReq, context.CancellationToken).ConfigureAwait(false);
                return AcpProtocolParser.BuildUrlElicitationReply(action);
            }

            var question = AcpProtocolParser.ParseFormElicitation(parameters, context.RawId);
            if (question is null)
            {
                return new JsonObject { ["action"] = "decline" };
            }
            if (_options.OnFormElicitationRequest is not null)
            {
                var resp = await _options.OnFormElicitationRequest(question, context.CancellationToken).ConfigureAwait(false);
                return AcpProtocolParser.BuildFormElicitationReply(question, resp);
            }
            var answers = _options.OnQuestionRequest is not null
                ? await _options.OnQuestionRequest(question, context.CancellationToken).ConfigureAwait(false)
                : null;
            return AcpProtocolParser.BuildFormElicitationReply(question, answers);
        }

        // 3. Standard ACP permission request.
        if (method == "session/request_permission")
        {
            var req = AcpProtocolParser.ParsePermissionRequest(parameters, context.RawId, _options.Dialect)
                ?? throw new AcpRpcException(-32602, "Invalid ACP permission request.");
            if (_started is not null && !string.IsNullOrEmpty(req.SessionId) && req.SessionId != _started.SessionId)
            {
                return AcpPermissionCoordinator.BuildCancelledReply();
            }
            if (dialectHandled?.ApprovalNotice is { Length: > 0 } notice)
            {
                if (dialectHandled.RequireManualApproval) req.WarningMessage = notice;
                else req.ActionDetail = notice;
            }
            if (dialectHandled?.IsPlanApproval == true)
            {
                req.IsPlanApproval = true;
                req.PlanMarkdown ??= dialectHandled.PlanMarkdown ?? req.ToolCall.PlanMarkdown;
            }
            else if (string.Equals(req.ToolCall.WorkflowAction, "exit_plan", StringComparison.Ordinal))
            {
                req.IsPlanApproval = true;
                req.PlanMarkdown ??= req.ToolCall.PlanMarkdown;
            }
            var mode = _options.GetActivePermissionMode?.Invoke() ?? PermissionModes.Default;
            var auto = AcpPermissionCoordinator.TryAutoSelectOptionId(
                req, mode, _options.PermissionStrategy,
                requireManualApproval: dialectHandled?.RequireManualApproval == true || req.IsPlanApproval);
            if (auto is not null)
            {
                return AcpPermissionCoordinator.BuildSelectedReply(auto);
            }
            if (_options.OnPermissionRequest is null)
            {
                return AcpPermissionCoordinator.BuildCancelledReply();
            }
            var chosen = await _options.OnPermissionRequest(req, context.CancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(chosen)
                ? AcpPermissionCoordinator.BuildCancelledReply()
                : AcpPermissionCoordinator.BuildSelectedReply(chosen);
        }

        // 4. Client-hosted filesystem callbacks (fs/read_text_file, fs/write_text_file) when explicitly enabled.
        if (_options.EnableClientFileSystem && _options.FileSystemHandler is { } fsHandler)
        {
            if (method == "fs/read_text_file")
            {
                if (parameters is not { ValueKind: JsonValueKind.Object } obj)
                    throw new AcpRpcException(-32602, "Invalid fs/read_text_file params.");
                var sid = AcpProtocolParser.TryStr(obj, "sessionId") ?? "";
                var path = AcpProtocolParser.TryStr(obj, "path") ?? "";
                var line = AcpProtocolParser.TryInt(obj, "line");
                var limit = AcpProtocolParser.TryInt(obj, "limit");
                var content = await fsHandler.ReadTextFileAsync(sid, path, line, limit, context.CancellationToken).ConfigureAwait(false);
                return new JsonObject { ["content"] = content };
            }
            if (method == "fs/write_text_file")
            {
                if (parameters is not { ValueKind: JsonValueKind.Object } obj)
                    throw new AcpRpcException(-32602, "Invalid fs/write_text_file params.");
                var sid = AcpProtocolParser.TryStr(obj, "sessionId") ?? "";
                var path = AcpProtocolParser.TryStr(obj, "path") ?? "";
                var content = AcpProtocolParser.TryStr(obj, "content")
                    ?? throw new AcpRpcException(-32602, "fs/write_text_file requires string content.");
                await fsHandler.WriteTextFileAsync(sid, path, content, context.CancellationToken).ConfigureAwait(false);
                return new JsonObject();
            }
        }

        // 5. Client-hosted terminal callbacks (terminal/*) when explicitly enabled.
        if (_options.EnableClientTerminal && _options.TerminalHandler is { } termHandler)
        {
            if (method == "terminal/create")
            {
                if (parameters is not { ValueKind: JsonValueKind.Object } obj)
                    throw new AcpRpcException(-32602, "Invalid terminal/create params.");
                var sid = AcpProtocolParser.TryStr(obj, "sessionId") ?? "";
                var command = AcpProtocolParser.TryStr(obj, "command") ?? "";
                List<string>? args = null;
                if (AcpProtocolParser.TryArr(obj, "args") is { } argsEl)
                {
                    args = argsEl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToList();
                }
                Dictionary<string, string>? env = null;
                if (AcpProtocolParser.TryArr(obj, "env") is { } envArr)
                {
                    env = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var entry in envArr.EnumerateArray())
                    {
                        var name = AcpProtocolParser.TryStr(entry, "name");
                        var val = AcpProtocolParser.TryStr(entry, "value");
                        if (!string.IsNullOrWhiteSpace(name) && val is not null) env[name] = val;
                    }
                }
                else if (AcpProtocolParser.TryObj(obj, "env") is { } envObj)
                {
                    env = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var prop in envObj.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String) env[prop.Name] = prop.Value.GetString()!;
                    }
                }
                var cwd = AcpProtocolParser.TryStr(obj, "cwd");
                var byteLimit = AcpProtocolParser.TryInt(obj, "outputByteLimit");
                var termId = await termHandler.CreateAsync(sid, command, args, env, cwd, byteLimit, context.CancellationToken).ConfigureAwait(false);
                return new JsonObject { ["terminalId"] = termId };
            }
            if (method == "terminal/output")
            {
                if (parameters is not { ValueKind: JsonValueKind.Object } obj)
                    throw new AcpRpcException(-32602, "Invalid terminal/output params.");
                var sid = AcpProtocolParser.TryStr(obj, "sessionId") ?? "";
                var termId = AcpProtocolParser.TryStr(obj, "terminalId") ?? "";
                var res = await termHandler.GetOutputAsync(sid, termId, context.CancellationToken).ConfigureAwait(false);
                var reply = new JsonObject
                {
                    ["output"] = res.Output,
                    ["truncated"] = res.Truncated,
                };
                if (res.ExitStatus is { } exit)
                {
                    reply["exitStatus"] = SerializeExitStatus(exit);
                }
                return reply;
            }
            if (method == "terminal/wait_for_exit")
            {
                if (parameters is not { ValueKind: JsonValueKind.Object } obj)
                    throw new AcpRpcException(-32602, "Invalid terminal/wait_for_exit params.");
                var sid = AcpProtocolParser.TryStr(obj, "sessionId") ?? "";
                var termId = AcpProtocolParser.TryStr(obj, "terminalId") ?? "";
                var exit = await termHandler.WaitForExitAsync(sid, termId, context.CancellationToken).ConfigureAwait(false);
                return SerializeExitStatus(exit);
            }
            if (method == "terminal/kill")
            {
                if (parameters is not { ValueKind: JsonValueKind.Object } obj)
                    throw new AcpRpcException(-32602, "Invalid terminal/kill params.");
                var sid = AcpProtocolParser.TryStr(obj, "sessionId") ?? "";
                var termId = AcpProtocolParser.TryStr(obj, "terminalId") ?? "";
                await termHandler.KillAsync(sid, termId, context.CancellationToken).ConfigureAwait(false);
                return new JsonObject();
            }
            if (method == "terminal/release")
            {
                if (parameters is not { ValueKind: JsonValueKind.Object } obj)
                    throw new AcpRpcException(-32602, "Invalid terminal/release params.");
                var sid = AcpProtocolParser.TryStr(obj, "sessionId") ?? "";
                var termId = AcpProtocolParser.TryStr(obj, "terminalId") ?? "";
                await termHandler.ReleaseAsync(sid, termId, context.CancellationToken).ConfigureAwait(false);
                return new JsonObject();
            }
        }

        throw new AcpRpcException(-32601, $"Unsupported ACP client method: {method}");
    }

    private static JsonObject SerializeExitStatus(AcpTerminalExitStatus exit)
    {
        var obj = new JsonObject();
        if (exit.ExitCode.HasValue) obj["exitCode"] = exit.ExitCode.Value;
        if (!string.IsNullOrEmpty(exit.Signal)) obj["signal"] = exit.Signal;
        return obj;
    }

    private void HandleNotification(string method, JsonElement? parameters)
    {
        if (method == "elicitation/complete")
        {
            if (parameters is { ValueKind: JsonValueKind.Object } elObj
                && AcpProtocolParser.TryStr(elObj, "elicitationId") is { Length: > 0 } elicitId)
            {
                Emit(new AcpElicitationCompletedEvent(elicitId) { SessionId = _started?.SessionId });
            }
            return;
        }

        if (method != "session/update")
        {
            if (_options.Dialect.TryTranslateNotification(method, parameters) is { } translated)
            {
                Emit(translated);
            }
            return;
        }

        if (parameters is not { ValueKind: JsonValueKind.Object } env) return;
        if (AcpProtocolParser.TryObj(env, "update") is not { } upd) return;
        var kind = AcpProtocolParser.TryStr(upd, "sessionUpdate");
        if (string.IsNullOrEmpty(kind)) return;

        var sessionId = AcpProtocolParser.TryStr(env, "sessionId") ?? _started?.SessionId;

        // During session/load the agent replays the prior conversation; forward it tagged as replay so the
        // client can rebuild an empty timeline (and skip it when the history is already on screen).
        var isReplay = _isLoadingHistory
            || AcpProtocolParser.TryBool(AcpProtocolParser.TryObj(env, "_meta"), "isReplay") == true;

        switch (kind)
        {
            case "user_message_chunk" when isReplay:
            {
                var msgId = AcpProtocolParser.TryStr(upd, "messageId");
                if (AcpProtocolParser.TryObj(upd, "content") is { } block)
                {
                    if (AcpProtocolParser.ExtractImageContent(block) is { } img)
                    {
                        Emit(new AcpUserImageEvent(img, msgId) { IsReplay = true, SessionId = sessionId });
                    }
                    else
                    {
                        var text = AcpProtocolParser.ExtractContentBlockText(block);
                        if (!string.IsNullOrEmpty(text))
                        {
                            Emit(new AcpUserMessageEvent(text, msgId) { IsReplay = true, SessionId = sessionId });
                        }
                    }
                }
                break;
            }
            case "agent_message_chunk":
            case "agent_thought_chunk":
            {
                var channel = kind == "agent_thought_chunk" ? "reasoning" : "assistant";
                var messageId = AcpProtocolParser.TryStr(upd, "messageId");
                if (AcpProtocolParser.TryObj(upd, "content") is { } block)
                {
                    if (kind == "agent_message_chunk" && AcpProtocolParser.ExtractImageContent(block) is { } img)
                    {
                        Emit(new AcpImageEvent(img, messageId) { IsReplay = isReplay, SessionId = sessionId });
                    }
                    else if (kind == "agent_message_chunk" && AcpProtocolParser.ExtractAudioContent(block) is { } audio)
                    {
                        Emit(new AcpAudioEvent(audio, messageId) { IsReplay = isReplay, SessionId = sessionId });
                    }
                    else
                    {
                        var text = AcpProtocolParser.ExtractContentBlockText(block);
                        if (!string.IsNullOrEmpty(text))
                        {
                            Emit(new AcpTextDeltaEvent(channel, text, messageId) { IsReplay = isReplay, SessionId = sessionId });
                        }
                    }
                }
                break;
            }
            case "tool_call":
            case "tool_call_update":
            {
                var tc = AcpProtocolParser.ParseToolCallUpdate(upd, _options.Dialect);
                Emit(new AcpToolCallEvent(tc) { IsReplay = isReplay, SessionId = sessionId });
                break;
            }
            case "image_generation_begin":
            case "image_generation_partial":
            case "image_generation_update":
            case "image_generation_end":
            {
                var tc = AcpProtocolParser.ParseImageGenerationNotification(kind, upd, _options.Dialect);
                Emit(new AcpToolCallEvent(tc) { IsReplay = isReplay, SessionId = sessionId });
                break;
            }
            case "plan":
            {
                if (AcpProtocolParser.TryArr(upd, "entries") is { } entries)
                {
                    Emit(new AcpPlanEvent(AcpProtocolParser.ParsePlanEntries(entries)) { IsReplay = isReplay, SessionId = sessionId });
                }
                break;
            }
            case "plan_update":
            {
                if (AcpProtocolParser.TryObj(upd, "plan") is { } plan)
                {
                    var pType = AcpProtocolParser.TryStr(plan, "type");
                    if (pType == "items" && AcpProtocolParser.TryArr(plan, "entries") is { } entries)
                    {
                        Emit(new AcpPlanEvent(AcpProtocolParser.ParsePlanEntries(entries)) { IsReplay = isReplay, SessionId = sessionId });
                    }
                    else if (pType == "markdown" && AcpProtocolParser.TryStr(plan, "content") is { Length: > 0 } md)
                    {
                        Emit(new AcpPlanEvent([], md) { IsReplay = isReplay, SessionId = sessionId });
                    }
                }
                break;
            }
            case "plan_removed":
            {
                Emit(new AcpPlanRemovedEvent { IsReplay = isReplay, SessionId = sessionId });
                break;
            }
            case "usage_update":
            {
                Emit(new AcpUsageEvent(AcpProtocolParser.ParseUsageUpdate(upd)) { SessionId = sessionId });
                break;
            }
            case "available_commands_update":
            {
                if (AcpProtocolParser.TryArr(upd, "availableCommands") is { } cmds)
                {
                    _commands = AcpProtocolParser.ParseAvailableCommands(cmds, _options.Dialect);
                    Emit(new AcpCommandsUpdatedEvent(_commands) { SessionId = sessionId });
                }
                break;
            }
            case "current_mode_update":
            {
                var modeId = AcpProtocolParser.TryStr(upd, "currentModeId");
                if (!string.IsNullOrEmpty(modeId))
                {
                    _modes ??= new AcpSessionModeState();
                    _modes.CurrentModeId = modeId;
                    Emit(new AcpModesUpdatedEvent(_modes) { SessionId = sessionId });
                }
                break;
            }
            case "current_model_update":
            {
                var modelId = AcpProtocolParser.TryStr(upd, "currentModelId") ?? AcpProtocolParser.TryStr(upd, "modelId");
                if (!string.IsNullOrEmpty(modelId))
                {
                    _models ??= new AcpSessionModelState();
                    _models.CurrentModelId = modelId;
                    Emit(new AcpModelsUpdatedEvent(_models) { SessionId = sessionId });
                }
                break;
            }
            case "config_option_update":
            {
                if (AcpProtocolParser.TryArr(upd, "configOptions") is { } opts)
                {
                    _configOptions = AcpProtocolParser.ParseConfigOptions(opts);
                    Emit(new AcpConfigOptionsUpdatedEvent(_configOptions) { SessionId = sessionId });
                }
                break;
            }
            case "session_info_update":
            {
                var info = AcpProtocolParser.ParseSessionInfoUpdate(upd);
                Emit(new AcpSessionInfoUpdatedEvent(info) { SessionId = sessionId });
                if (!string.IsNullOrWhiteSpace(info.Title))
                {
                    Emit(new AcpTitleUpdatedEvent(info.Title.Trim()) { SessionId = sessionId });
                }
                break;
            }
            case "compaction_update":
            case "compaction_summary_chunk":
            {
                var comp = AcpProtocolParser.ParseCompactionUpdate(kind, upd);
                Emit(new AcpCompactionEvent(comp) { IsReplay = isReplay, SessionId = sessionId });
                break;
            }
            case "cursor_update":
            {
                if (AcpProtocolParser.ParseCursorUpdate(upd) is { } cursor)
                {
                    Emit(new AcpCursorUpdatedEvent(cursor) { IsReplay = isReplay, SessionId = sessionId });
                }
                break;
            }
        }
    }

    private void Emit(AcpRuntimeEvent evt)
    {
        try { EventReceived?.Invoke(evt); }
        catch { /* Listeners cannot break notification dispatch. */ }
    }
}
