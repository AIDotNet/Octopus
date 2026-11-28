using System.Text.Json.Serialization;

namespace Octopus.Core.Agents;

/// <summary>Wire protocol spoken by a managed model provider (design/20-模型提供商与模型托管.md §3.1).</summary>
public enum ProviderProtocol
{
    /// <summary>Anthropic Messages API (<c>/v1/messages</c>).</summary>
    Anthropic,
    /// <summary>OpenAI Responses API (<c>/v1/responses</c>, preferred by Codex CLI).</summary>
    OpenAIResponses,
    /// <summary>OpenAI Chat Completions API (<c>/v1/chat/completions</c>).</summary>
    OpenAIChat,
    /// <summary>Google Gemini API with custom base URL support.</summary>
    Gemini,
    /// <summary>Official Google <c>GEMINI_API_KEY</c> without a custom gateway URL (usable by Gemini CLI and Antigravity CLI).</summary>
    GeminiOfficialKey,
}

/// <summary>Optional role tier for a managed model, used to populate agent-internal model slots (e.g. Claude Opus/Sonnet/Haiku).</summary>
public enum ModelSlotTier
{
    None,
    Flagship,
    Balanced,
    Fast,
}

/// <summary>
/// How an agent CLI consumes a managed model provider (design/20-模型提供商与模型托管.md §3.3).
/// Null on <see cref="AgentDefinition.ProviderSupport"/> means the agent does not expose managed provider switching.
/// </summary>
public sealed class AgentProviderCapability
{
    /// <summary>Protocols this agent can talk to directly: <c>anthropic</c>, <c>openAIResponses</c>, <c>openAIChat</c>, <c>gemini</c>, <c>geminiOfficialKey</c>.</summary>
    public List<string> Protocols { get; set; } = [];

    /// <summary>Materialization strategy: <c>env</c>, <c>codexHomeOverlay</c>, or <c>opencodeConfigOverlay</c>.</summary>
    public string Strategy { get; set; } = ProviderMaterializeStrategies.Env;

    /// <summary>For <c>env</c> strategy: environment variable holding the provider's base URL (e.g. <c>ANTHROPIC_BASE_URL</c>).</summary>
    public string? BaseUrlEnv { get; set; }

    /// <summary>For <c>env</c> strategy: primary environment variable holding the API key/token.</summary>
    public string? ApiKeyEnv { get; set; }

    /// <summary>For <c>env</c> strategy: optional alternate API key environment variable selectable per provider (e.g. <c>ANTHROPIC_API_KEY</c>).</summary>
    public string? AltApiKeyEnv { get; set; }

    /// <summary>Optional environment variable populated with the chosen model id.</summary>
    public string? ModelEnv { get; set; }

    /// <summary>Optional map from tier id (<c>flagship</c>, <c>balanced</c>, <c>fast</c>) to agent env var (e.g. <c>ANTHROPIC_DEFAULT_OPUS_MODEL</c>).</summary>
    public Dictionary<string, string>? TierModelEnvs { get; set; }

    /// <summary>Optional config-directory env var isolated per provider so OAuth subscription state does not collide (e.g. <c>CLAUDE_CONFIG_DIR</c>).</summary>
    public string? IsolateConfigDirEnv { get; set; }

    /// <summary>Optional prefix prepended to model ids passed on the CLI (e.g. <c>octopus/</c> for OpenCode or <c>openai/</c> for Aider).</summary>
    public string? ModelPrefix { get; set; }

    /// <summary>Optional protocol-keyed model prefix overrides (e.g. Aider: <c>anthropic</c> -> <c>anthropic/</c>, <c>openAIChat</c> -> <c>openai/</c>).</summary>
    public Dictionary<string, string>? ProtocolModelPrefixes { get; set; }

    /// <summary>Optional protocol-keyed base URL env overrides (for multi-protocol <c>env</c> agents like Aider/Goose).</summary>
    public Dictionary<string, string>? ProtocolBaseUrlEnvs { get; set; }

    /// <summary>Optional protocol-keyed API key env overrides.</summary>
    public Dictionary<string, string>? ProtocolApiKeyEnvs { get; set; }

    /// <summary>Optional protocol-keyed static env vars injected when using that protocol (e.g. Goose <c>GOOSE_PROVIDER=openai</c>).</summary>
    public Dictionary<string, Dictionary<string, string>>? ProtocolExtraEnv { get; set; }

    /// <summary>Host/shell env vars unset when launching with a managed provider so stale credentials do not override.</summary>
    public List<string> UnsetEnv { get; set; } = [];

    public bool SupportsProtocol(ProviderProtocol protocol)
    {
        var token = ProviderProtocolNames.ToConfigName(protocol);
        return Protocols.Any(p => string.Equals(p, token, StringComparison.OrdinalIgnoreCase));
    }
}

public static class ProviderMaterializeStrategies
{
    public const string Env = "env";
    public const string CodexHomeOverlay = "codexHomeOverlay";
    public const string OpenCodeConfigOverlay = "opencodeConfigOverlay";
}

public static class ProviderProtocolNames
{
    public const string Anthropic = "anthropic";
    public const string OpenAIResponses = "openaiResponses";
    public const string OpenAIChat = "openaiChat";
    public const string Gemini = "gemini";
    public const string GeminiOfficialKey = "geminiOfficialKey";

    public static string ToConfigName(ProviderProtocol protocol) => protocol switch
    {
        ProviderProtocol.Anthropic => Anthropic,
        ProviderProtocol.OpenAIResponses => OpenAIResponses,
        ProviderProtocol.OpenAIChat => OpenAIChat,
        ProviderProtocol.Gemini => Gemini,
        ProviderProtocol.GeminiOfficialKey => GeminiOfficialKey,
        _ => Anthropic,
    };

    public static string DisplayLabel(ProviderProtocol protocol) => protocol switch
    {
        ProviderProtocol.Anthropic => "Anthropic",
        ProviderProtocol.OpenAIResponses => "OpenAI Responses",
        ProviderProtocol.OpenAIChat => "OpenAI Chat",
        ProviderProtocol.Gemini => "Gemini",
        ProviderProtocol.GeminiOfficialKey => "Gemini Official Key",
        _ => protocol.ToString(),
    };
}

/// <summary>Non-secret metadata for one managed model provider in <c>providers.json</c>. API keys live exclusively in <c>ISecretStore</c>.</summary>
public sealed class ModelProviderRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ProviderProtocol Protocol { get; set; } = ProviderProtocol.Anthropic;
    public string BaseUrl { get; set; } = "";
    /// <summary>Optional override of which API key env var to inject for <c>env</c> agents (e.g. <c>ANTHROPIC_API_KEY</c> vs <c>ANTHROPIC_AUTH_TOKEN</c>).</summary>
    public string? AuthEnvPreference { get; set; }
    /// <summary>Non-secret custom HTTP headers forwarded alongside requests.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];
    public bool Enabled { get; set; } = true;
    /// <summary>Isolates config directory per provider when supported by the CLI so official OAuth sessions do not conflict.</summary>
    public bool IsolateConfigDir { get; set; } = true;
    /// <summary>Allows sharing this provider over Octopus's end-to-end encrypted tunnel to remote hosts controlled from this machine.</summary>
    public bool AllowRemoteTunnel { get; set; } = true;
    public List<ManagedModelSpec> Models { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ModelsSyncedAt { get; set; }
}

/// <summary>One model entry hosted under a <see cref="ModelProviderRecord"/>.</summary>
public sealed class ManagedModelSpec
{
    public string Id { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; } = true;
    public ModelSlotTier Tier { get; set; } = ModelSlotTier.None;
    /// <summary>Supported reasoning effort levels (e.g. <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, <c>max</c>); null/empty allows all.</summary>
    public List<string>? SupportedEfforts { get; set; }
    public int? ContextWindow { get; set; }

    [JsonIgnore]
    public string EffectiveLabel => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName!;
}

/// <summary>A pinned quick-launch combination shown at the top of the "+" menu (Workspace Scheme C).</summary>
public sealed class PinnedLaunchPreset
{
    public string Id { get; set; } = "";
    public string AgentId { get; set; } = "";
    /// <summary>Unified backend reference (<c>provider:&lt;id&gt;</c>, <c>account:&lt;id&gt;</c>, or <c>tunnel:&lt;id&gt;</c>).</summary>
    public string BackendRef { get; set; } = "";
    public string? ModelId { get; set; }
    public string? Effort { get; set; }
    public string? CustomLabel { get; set; }
}

/// <summary>The content of <c>&lt;DataDirectory&gt;/providers.json</c>. Contains zero API keys or secrets.</summary>
public sealed class ProvidersDocument
{
    public int Version { get; set; } = 1;
    public List<ModelProviderRecord> Providers { get; set; } = [];
    public List<PinnedLaunchPreset> PinnedPresets { get; set; } = [];
}

/// <summary>
/// Non-secret provider descriptor sent from controller to remote host when starting a remote session backed by
/// an encrypted controller API tunnel (design/20-模型提供商与模型托管.md §6.2). Never contains real API keys or upstream URLs.
/// </summary>
public sealed class RemoteTunnelProviderSpec
{
    public string GrantToken { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public ProviderProtocol Protocol { get; set; } = ProviderProtocol.Anthropic;
    public string? AuthEnvPreference { get; set; }
    public bool IsolateConfigDir { get; set; } = true;
    public List<ManagedModelSpec> Models { get; set; } = [];
}

/// <summary>Helpers for constructing and parsing unified <c>BackendRef</c> strings (<c>account:*</c>, <c>provider:*</c>, <c>tunnel:*</c>).</summary>
public static class AgentBackendRefs
{
    public const string AccountPrefix = "account:";
    public const string ProviderPrefix = "provider:";
    public const string TunnelPrefix = "tunnel:";

    public static string Account(string accountId) => AccountPrefix + accountId;
    public static string Provider(string providerId) => ProviderPrefix + providerId;
    public static string Tunnel(string providerId) => TunnelPrefix + providerId;

    public static bool TryParseAccount(string? value, out string accountId) => TryStripPrefix(value, AccountPrefix, out accountId);
    public static bool TryParseProvider(string? value, out string providerId) => TryStripPrefix(value, ProviderPrefix, out providerId);
    public static bool TryParseTunnel(string? value, out string providerId) => TryStripPrefix(value, TunnelPrefix, out providerId);

    private static bool TryStripPrefix(string? value, string prefix, out string id)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.StartsWith(prefix, StringComparison.Ordinal))
        {
            var rest = value[prefix.Length..].Trim();
            if (rest.Length > 0)
            {
                id = rest;
                return true;
            }
        }
        id = "";
        return false;
    }
}
