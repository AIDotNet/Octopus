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
}

/// <summary>A user prompt chunk; only emitted for replayed history (live prompts are rendered by the client itself).</summary>
public sealed record AcpUserMessageEvent(string Text, string? MessageId) : AcpRuntimeEvent;
public sealed record AcpTextDeltaEvent(string Channel, string Text, string? MessageId) : AcpRuntimeEvent;
public sealed record AcpToolCallEvent(AcpToolCallUpdate Update) : AcpRuntimeEvent;
public sealed record AcpPlanEvent(IReadOnlyList<AcpPlanEntry> Entries, string? Markdown = null) : AcpRuntimeEvent;
public sealed record AcpUsageEvent(AcpContextUsage Usage) : AcpRuntimeEvent;
public sealed record AcpCommandsUpdatedEvent(IReadOnlyList<AcpAvailableCommand> Commands) : AcpRuntimeEvent;
public sealed record AcpModesUpdatedEvent(AcpSessionModeState Modes) : AcpRuntimeEvent;
public sealed record AcpConfigOptionsUpdatedEvent(IReadOnlyList<AcpSessionConfigOption> Options) : AcpRuntimeEvent;
public sealed record AcpModelsUpdatedEvent(AcpSessionModelState Models) : AcpRuntimeEvent;
public sealed record AcpTitleUpdatedEvent(string Title) : AcpRuntimeEvent;
public sealed record AcpDiagnosticEvent(string Message) : AcpRuntimeEvent;
public sealed record AcpClosedEvent(Exception Reason) : AcpRuntimeEvent;

/// <summary>Options controlling an <see cref="AcpSessionRuntime"/> instance.</summary>
public sealed class AcpSessionRuntimeOptions
{
    public IAcpDialect Dialect { get; init; } = GenericAcpDialect.Instance;
    public AcpPermissionStrategy PermissionStrategy { get; init; } = AcpPermissionStrategy.ClientApproval;
    public bool EnableFormElicitation { get; init; } = true;
    public string? AgentVersion { get; init; }
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public Func<string>? GetActivePermissionMode { get; init; }
    public Func<AcpPermissionRequest, CancellationToken, Task<string?>>? OnPermissionRequest { get; init; }
    public Func<AcpQuestionRequest, CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?>>? OnQuestionRequest { get; init; }
}

/// <summary>
/// Full ACP v1 session runtime over a bidirectional <see cref="AcpJsonRpcPeer"/>.
/// Handles initialize, session/new|load|resume with automatic authentication retry, prompt streaming,
/// live mode/model/config changes, and safe permission + elicitation callbacks.
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
    {
        _options = options ?? new AcpSessionRuntimeOptions();
        _peer = new AcpJsonRpcPeer(input, output, new AcpPeerHandlers
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

        var clientCaps = new JsonObject
        {
            ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
            ["terminal"] = false,
        };
        if (_options.EnableFormElicitation && _options.Dialect.AdvertiseFormElicitation)
        {
            clientCaps["elicitation"] = new JsonObject { ["form"] = new JsonObject() };
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

        var req = new JsonObject
        {
            ["protocolVersion"] = AcpProtocolConstants.ProtocolVersion,
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
        if (parsed.ProtocolVersion != AcpProtocolConstants.ProtocolVersion)
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

        async Task<AcpSessionStartResult> OpenAsync()
        {
            var baseParams = new JsonObject
            {
                ["cwd"] = workingDirectory,
                ["mcpServers"] = new JsonArray(),
            };
            if (additionalDirectories is { Count: > 0 } && init.AgentCapabilities.SessionCapabilities.SupportsAdditionalDirectories)
            {
                var dirs = new JsonArray();
                foreach (var d in additionalDirectories) dirs.Add((JsonNode)JsonValue.Create(d));
                baseParams["additionalDirectories"] = dirs;
            }

            if (!string.IsNullOrWhiteSpace(resumeSessionId))
            {
                if (init.AgentCapabilities.LoadSession)
                {
                    baseParams["sessionId"] = resumeSessionId;
                    _isLoadingHistory = true;
                    try
                    {
                        var loaded = await _peer.RequestAsync("session/load", baseParams, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                        return AcpProtocolParser.ParseSessionStart("load", resumeSessionId, loaded);
                    }
                    finally { _isLoadingHistory = false; }
                }
                if (init.AgentCapabilities.SessionCapabilities.SupportsResume)
                {
                    baseParams["sessionId"] = resumeSessionId;
                    var resumed = await _peer.RequestAsync("session/resume", baseParams, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                    return AcpProtocolParser.ParseSessionStart("resume", resumeSessionId, resumed);
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
            await _peer.RequestAsync("authenticate", new JsonObject { ["methodId"] = authId }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            started = await OpenAsync().ConfigureAwait(false);
        }

        _started = started;
        if (started.Modes is not null)
        {
            _modes = started.Modes;
            Emit(new AcpModesUpdatedEvent(started.Modes));
        }
        if (started.ConfigOptions.Count > 0)
        {
            _configOptions = started.ConfigOptions;
            Emit(new AcpConfigOptionsUpdatedEvent(_configOptions));
        }
        if (started.Models is not null)
        {
            _models = started.Models;
            Emit(new AcpModelsUpdatedEvent(started.Models));
        }
        return started;
    }

    public async Task<AcpPromptResult> PromptAsync(
        string text,
        IReadOnlyList<AcpPromptAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = RequireSessionId();
        var effectiveText = RewritePromptCommand(text);
        var imageSupported = _initialized?.AgentCapabilities.PromptCapabilities.Image == true;
        var blocks = AcpProtocolParser.BuildPromptBlocks(effectiveText, attachments, _options.Dialect, imageSupported);

        var req = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = blocks,
        };

        var raw = await _peer.RequestAsync("session/prompt", req, timeout: null, cancellationToken).ConfigureAwait(false);
        return AcpProtocolParser.ParsePromptResult(raw);
    }

    public Task CancelAsync()
    {
        if (_started is null) return Task.CompletedTask;
        _peer.CancelIncomingRequests();
        return _peer.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = _started.SessionId });
    }

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
                    Emit(new AcpPlanEvent([], dialectHandled.PlanMarkdown));
                }
                return dialectHandled.ImmediateReply;
            }
            if (dialectHandled.Question is not null && dialectHandled.BuildQuestionReply is not null)
            {
                var answers = _options.OnQuestionRequest is not null
                    ? await _options.OnQuestionRequest(dialectHandled.Question, context.CancellationToken).ConfigureAwait(false)
                    : null;
                return dialectHandled.BuildQuestionReply(answers);
            }
        }

        // 2. Standard ACP form elicitation (never auto-approved).
        if (method == "elicitation/create")
        {
            var question = AcpProtocolParser.ParseFormElicitation(parameters, context.RawId);
            if (question is null)
            {
                return new JsonObject { ["action"] = "decline" };
            }
            var answers = _options.OnQuestionRequest is not null
                ? await _options.OnQuestionRequest(question, context.CancellationToken).ConfigureAwait(false)
                : null;
            return AcpProtocolParser.BuildFormElicitationReply(question, answers);
        }

        // 3. Standard ACP permission request.
        if (method == "session/request_permission")
        {
            var req = AcpProtocolParser.ParsePermissionRequest(parameters, context.RawId, _options.Dialect);
            if (req is null)
            {
                throw new AcpRpcException(-32602, "Invalid ACP permission request.");
            }
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

        throw new AcpRpcException(-32601, $"Unsupported ACP client method: {method}");
    }

    private void HandleNotification(string method, JsonElement? parameters)
    {
        if (method != "session/update" || parameters is not { ValueKind: JsonValueKind.Object } env)
        {
            return;
        }
        if (AcpProtocolParser.TryObj(env, "update") is not { } upd) return;
        var kind = AcpProtocolParser.TryStr(upd, "sessionUpdate");
        if (string.IsNullOrEmpty(kind)) return;

        // During session/load the agent replays the prior conversation; forward it tagged as replay so the
        // client can rebuild an empty timeline (and skip it when the history is already on screen).
        var isReplay = _isLoadingHistory
            || AcpProtocolParser.TryBool(AcpProtocolParser.TryObj(env, "_meta"), "isReplay") == true;

        switch (kind)
        {
            case "user_message_chunk" when isReplay:
            {
                var text = AcpProtocolParser.TryObj(upd, "content") is { } block
                    ? AcpProtocolParser.ExtractContentBlockText(block)
                    : null;
                if (!string.IsNullOrEmpty(text))
                {
                    Emit(new AcpUserMessageEvent(text, AcpProtocolParser.TryStr(upd, "messageId")) { IsReplay = true });
                }
                break;
            }
            case "agent_message_chunk":
            case "agent_thought_chunk":
            {
                var channel = kind == "agent_thought_chunk" ? "reasoning" : "assistant";
                var text = AcpProtocolParser.TryObj(upd, "content") is { } block
                    ? AcpProtocolParser.ExtractContentBlockText(block)
                    : null;
                if (!string.IsNullOrEmpty(text))
                {
                    Emit(new AcpTextDeltaEvent(channel, text, AcpProtocolParser.TryStr(upd, "messageId")) { IsReplay = isReplay });
                }
                break;
            }
            case "tool_call":
            case "tool_call_update":
            {
                var tc = AcpProtocolParser.ParseToolCallUpdate(upd, _options.Dialect);
                Emit(new AcpToolCallEvent(tc) { IsReplay = isReplay });
                break;
            }
            case "plan":
            {
                if (AcpProtocolParser.TryArr(upd, "entries") is { } entries)
                {
                    Emit(new AcpPlanEvent(AcpProtocolParser.ParsePlanEntries(entries)) { IsReplay = isReplay });
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
                        Emit(new AcpPlanEvent(AcpProtocolParser.ParsePlanEntries(entries)) { IsReplay = isReplay });
                    }
                    else if (pType == "markdown" && AcpProtocolParser.TryStr(plan, "content") is { Length: > 0 } md)
                    {
                        Emit(new AcpPlanEvent([], md) { IsReplay = isReplay });
                    }
                }
                break;
            }
            case "usage_update":
            {
                Emit(new AcpUsageEvent(AcpProtocolParser.ParseUsageUpdate(upd)));
                break;
            }
            case "available_commands_update":
            {
                if (AcpProtocolParser.TryArr(upd, "availableCommands") is { } cmds)
                {
                    _commands = AcpProtocolParser.ParseAvailableCommands(cmds, _options.Dialect);
                    Emit(new AcpCommandsUpdatedEvent(_commands));
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
                    Emit(new AcpModesUpdatedEvent(_modes));
                }
                break;
            }
            case "config_option_update":
            {
                if (AcpProtocolParser.TryArr(upd, "configOptions") is { } opts)
                {
                    _configOptions = AcpProtocolParser.ParseConfigOptions(opts);
                    Emit(new AcpConfigOptionsUpdatedEvent(_configOptions));
                }
                break;
            }
            case "session_info_update":
            {
                var title = AcpProtocolParser.TryStr(upd, "title");
                if (!string.IsNullOrWhiteSpace(title))
                {
                    Emit(new AcpTitleUpdatedEvent(title.Trim()));
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
