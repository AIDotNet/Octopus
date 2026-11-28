using Octopus.Core.Agents;
using Octopus.Core.Projects;

namespace Octopus.Agents.Providers;

/// <summary>Result of testing connectivity against a managed provider endpoint.</summary>
public sealed record ProviderTestResult(bool Succeeded, int LatencyMs, string? ErrorMessage = null, int? DiscoveredModelCount = null);

/// <summary>One built-in quick preset template shown in the "Model Providers" settings page (Scheme B).</summary>
public sealed record ProviderPreset(
    string Id,
    string Name,
    string NameZh,
    ProviderProtocol Protocol,
    string BaseUrl,
    string? AuthEnvPreference,
    IReadOnlyList<ManagedModelSpec> DefaultModels);

/// <summary>Output of materializing a managed provider (or remote tunnel descriptor) for a specific agent CLI.</summary>
public sealed record ProviderMaterialization(
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> UnsetEnvironment,
    string? ModelPrefix = null,
    string? IsolatedConfigDir = null);

/// <summary>Unified resolution result for an agent launch: either a subscription account or a managed/tunneled provider.</summary>
public sealed record ResolvedAgentBackend(
    string BackendRef,
    bool IsProvider,
    string DisplayName,
    AgentAccountRecord? Account,
    ModelProviderRecord? Provider,
    IReadOnlyDictionary<string, string>? Environment,
    IReadOnlyList<string>? UnsetEnvironment,
    string? ModelPrefix = null);

/// <summary>
/// Manages cross-agent model providers (<c>providers.json</c> + <c>ISecretStore</c>), connectivity testing,
/// model catalog syncing, and pinned quick-launch presets (design/20-模型提供商与模型托管.md §4.1).
/// </summary>
public interface IModelProviderService
{
    event EventHandler? Changed;

    IReadOnlyList<ModelProviderRecord> ListProviders();
    IReadOnlyList<ModelProviderRecord> ListCompatibleProviders(AgentDefinition definition, bool requireRemoteTunnel = false);
    ModelProviderRecord? Find(string providerId);
    IReadOnlyList<PinnedLaunchPreset> ListPinnedPresets();

    /// <summary>Saves a new or updated provider record (non-secret metadata) and optionally updates its secret in <c>ISecretStore</c>.</summary>
    Task<ModelProviderRecord> SaveAsync(ModelProviderRecord record, string? newApiKey = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a provider record, deletes its API key from <c>ISecretStore</c>, and cleans up pinned presets referencing it.</summary>
    Task RemoveAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Toggles a provider's enabled state.</summary>
    void SetEnabled(string providerId, bool enabled);

    Task<bool> HasSecretAsync(string providerId, CancellationToken cancellationToken = default);
    Task<string?> ReadSecretAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Tests reachability and authentication against the provider's endpoint.</summary>
    Task<ProviderTestResult> TestConnectionAsync(ModelProviderRecord record, string? overrideApiKey = null, CancellationToken cancellationToken = default);

    /// <summary>Fetches remote models from <c>/v1/models</c> or <c>/models</c> and returns parsed model specs.</summary>
    Task<IReadOnlyList<ManagedModelSpec>> FetchRemoteModelsAsync(ModelProviderRecord record, string? overrideApiKey = null, CancellationToken cancellationToken = default);

    /// <summary>Pins a quick-launch preset to the "+" launcher first level (Workspace Scheme C).</summary>
    PinnedLaunchPreset PinPreset(PinnedLaunchPreset preset);

    /// <summary>Removes a pinned quick-launch preset.</summary>
    void UnpinPreset(string presetId);
}

/// <summary>
/// Resolves the effective backend (subscription account vs managed provider) using 4-tier precedence
/// and materializes the launch environment without mutating the user's native CLI configuration files.
/// </summary>
public interface IAgentBackendResolver
{
    /// <summary>Resolves which <c>BackendRef</c> is preferred in UI for <paramref name="definition"/> given profile and project.</summary>
    string? ResolvePreferredBackendRef(AgentDefinition definition, AgentProfile profile, Project? project = null);

    /// <summary>Returns the human-readable display name of <paramref name="backendRef"/> for badges and sidebar subtitles.</summary>
    string? DescribeBackend(AgentDefinition definition, string? backendRef);

    /// <summary>Resolves and materializes the effective backend for launching an agent process (Terminal or ACP).</summary>
    Task<ResolvedAgentBackend> ResolveAsync(
        AgentDefinition definition,
        AgentProfile profile,
        Project? project = null,
        AgentLaunchOptions? launch = null,
        CancellationToken cancellationToken = default);
}
