using Octopus.Core.Agents;

namespace Octopus.Agents.Providers;

/// <summary>Well-known managed provider IDs automatically synced from a bound CoWork account.</summary>
public static class CoWorkProviderIds
{
    public const string Prefix = "cowork-";
    public const string Anthropic = "cowork-anthropic";
    public const string OpenAIResponses = "cowork-openai-responses";
    public const string OpenAIChat = "cowork-openai-chat";

    public static bool IsCoWorkProvider(string? providerId) =>
        !string.IsNullOrWhiteSpace(providerId)
        && providerId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Live loopback proxy endpoint and ephemeral per-process token injected into local agent launches.</summary>
public sealed record CoWorkRuntimeEndpoint(string BaseUrl, string LocalApiKey);

/// <summary>
/// Runtime bridge between <see cref="AgentBackendResolver"/> (in <c>Octopus.Agents</c>) and the app-level
/// CoWork binding + loopback proxy service (in <c>Octopus.App</c>).
/// </summary>
public interface ICoWorkProviderBridge
{
    /// <summary>
    /// Ensures the local CoWork loopback proxy is running and a valid delegated token is available, then returns
    /// the protocol-appropriate local base URL (<c>http://127.0.0.1:&lt;port&gt;</c> or <c>.../v1</c>) and local token.
    /// Returns null when CoWork is not bound or unavailable.
    /// </summary>
    Task<CoWorkRuntimeEndpoint?> ResolveRuntimeEndpointAsync(ProviderProtocol protocol, CancellationToken cancellationToken = default);
}
