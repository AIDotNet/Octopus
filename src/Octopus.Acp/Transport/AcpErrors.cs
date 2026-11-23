using System.Text.Json;
using Octopus.Acp.Protocol;

namespace Octopus.Acp.Transport;

/// <summary>Base error for ACP protocol and transport failures.</summary>
public class AcpException : Exception
{
    public AcpException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A JSON-RPC 2.0 error returned by the ACP agent.</summary>
public class AcpRpcException : AcpException
{
    public AcpRpcException(int code, string message, JsonElement? data = null) : base(message)
    {
        Code = code;
        ErrorData = data?.Clone();
    }

    public int Code { get; }
    public JsonElement? ErrorData { get; }

    /// <summary>Extracts a human-readable string from <see cref="ErrorData"/> when present.</summary>
    public string? DataSummary()
    {
        if (ErrorData is not { } data) return null;
        return data.ValueKind switch
        {
            JsonValueKind.String => data.GetString(),
            JsonValueKind.Object when data.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String => msg.GetString(),
            JsonValueKind.Object when data.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.String => det.GetString(),
            JsonValueKind.Object when data.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String => err.GetString(),
            _ => data.GetRawText(),
        };
    }
}

/// <summary>The ACP agent requires authentication before starting or continuing a session (JSON-RPC code -32000).</summary>
public sealed class AcpAuthRequiredException : AcpRpcException
{
    public AcpAuthRequiredException(
        string message,
        IReadOnlyList<AcpAuthMethod>? authMethods = null,
        JsonElement? data = null)
        : base(-32000, message, data)
    {
        AuthMethods = authMethods ?? [];
    }

    public IReadOnlyList<AcpAuthMethod> AuthMethods { get; }
}

/// <summary>The underlying ACP stdio connection was closed.</summary>
public sealed class AcpConnectionClosedException : AcpException
{
    public AcpConnectionClosedException(string message = "ACP connection closed.", Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>A JSON-RPC request did not receive a reply before its deadline.</summary>
public sealed class AcpTimeoutException : AcpException
{
    public AcpTimeoutException(string method, TimeSpan timeout)
        : base($"ACP request '{method}' timed out after {(int)timeout.TotalMilliseconds} ms.")
    {
        Method = method;
        Timeout = timeout;
    }

    public string Method { get; }
    public TimeSpan Timeout { get; }
}
