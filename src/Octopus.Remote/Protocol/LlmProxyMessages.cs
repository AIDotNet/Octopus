using System.Text.Json.Serialization;

namespace Octopus.Remote.Protocol;

/// <summary>
/// Non-secret provider descriptor sent from controller to remote host in <see cref="CreateItemMessage.TunnelProvider"/>
/// when launching a remote agent session backed by the controller's encrypted LLM API tunnel (design/20 §6.2).
/// Never contains real API keys or upstream provider URLs.
/// </summary>
public sealed class LlmTunnelProviderMessage
{
    public string GrantToken { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Protocol config name: <c>anthropic</c>, <c>openaiResponses</c>, <c>openaiChat</c>, <c>gemini</c>, <c>geminiOfficialKey</c>.</summary>
    public string Protocol { get; set; } = "anthropic";
    public string? AuthEnvPreference { get; set; }
    public bool IsolateConfigDir { get; set; } = true;
    public List<LlmTunnelModelMessage> Models { get; set; } = [];

    /// <summary>Runtime-only loopback base URL filled by the host connection before invoking <c>IHostWorkspace.CreateItemAsync</c>.</summary>
    [JsonIgnore]
    public string? LoopbackBaseUrl { get; set; }
}

/// <summary>One model entry inside <see cref="LlmTunnelProviderMessage"/>.</summary>
public sealed class LlmTunnelModelMessage
{
    public string Id { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Tier { get; set; }
    public List<string>? SupportedEfforts { get; set; }
}

/// <summary>
/// Reverse RPC request sent from remote host to controller (<see cref="RemoteMethods.LlmProxyOpen"/>)
/// to forward one local CLI HTTP/SSE request through the encrypted SecureChannel.
/// </summary>
public sealed class LlmProxyOpenMessage
{
    public string GrantToken { get; set; } = "";
    /// <summary>Host-allocated channel ID with high bit set (<c>0x8000_0000 | seq</c>) for streaming response (and large request body).</summary>
    public uint Channel { get; set; }
    public string Method { get; set; } = "POST";
    /// <summary>Relative path + optional query string (e.g. <c>/v1/messages</c>). Validated strictly against SSRF on controller.</summary>
    public string RelativePath { get; set; } = "/";
    /// <summary>Filtered request headers (auth and hop-by-hop headers already stripped on the remote host).</summary>
    public Dictionary<string, string> Headers { get; set; } = [];
    /// <summary>Base64-encoded request body when small (≤ 32 KiB); null when empty or streamed via <see cref="HasBodyStream"/>.</summary>
    public string? InlineBody { get; set; }
    /// <summary>When true, the remote host streams the request body over <see cref="Channel"/> using <c>StreamData</c>/<c>StreamEnd</c>.</summary>
    public bool HasBodyStream { get; set; }
}

/// <summary>Initial HTTP response head returned immediately for <see cref="RemoteMethods.LlmProxyOpen"/>; body follows on <c>StreamData</c>.</summary>
public sealed class LlmProxyOpenResultMessage
{
    public int StatusCode { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    /// <summary>Optional short error message when the proxy rejected the request before contacting upstream.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>Notification/RPC from remote host to controller cancelling an in-flight tunneled LLM request.</summary>
public sealed class LlmProxyCancelMessage
{
    public uint Channel { get; set; }
}

/// <summary>RPC from controller to remote host rebinding existing grant tokens to a freshly reconnected session.</summary>
public sealed class LlmProxyRebindMessage
{
    public List<string> GrantTokens { get; set; } = [];
}

/// <summary>RPC from controller to remote host revoking a grant token immediately.</summary>
public sealed class LlmProxyRevokeMessage
{
    public string GrantToken { get; set; } = "";
}
