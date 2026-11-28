using Octopus.Agents.Accounts;
using Octopus.Core.Agents;
using Octopus.Core.Projects;

namespace Octopus.Agents.Providers;

/// <summary>
/// Unifies <see cref="IAgentAccountService"/> (official subscription accounts) and <see cref="IModelProviderService"/>
/// (managed API providers) into a single 4-tier backend resolver (design/20-模型提供商与模型托管.md §3.2).
/// </summary>
public sealed class AgentBackendResolver(
    IModelProviderService providers,
    ProviderMaterializer materializer,
    IAgentAccountService? accounts = null,
    ICoWorkProviderBridge? cowork = null) : IAgentBackendResolver
{
    public string? ResolvePreferredBackendRef(AgentDefinition definition, AgentProfile profile, Project? project = null)
    {
        // 1. Project-level backend (new AgentBackends takes precedence over legacy AgentAccounts)
        if (project is not null)
        {
            if (project.AgentBackends.TryGetValue(definition.Id, out var projBackend)
                && IsUsableBackend(definition, projBackend))
            {
                return projBackend;
            }
            if (project.AgentAccounts.TryGetValue(definition.Id, out var projAcc)
                && !string.IsNullOrWhiteSpace(projAcc))
            {
                var accRef = AgentBackendRefs.Account(projAcc);
                if (IsUsableBackend(definition, accRef)) return accRef;
            }
        }

        // 2. Profile-level default backend (new DefaultBackend takes precedence over legacy DefaultAccountId)
        if (!string.IsNullOrWhiteSpace(profile.DefaultBackend)
            && IsUsableBackend(definition, profile.DefaultBackend))
        {
            return profile.DefaultBackend;
        }
        if (!string.IsNullOrWhiteSpace(profile.DefaultAccountId))
        {
            var accRef = AgentBackendRefs.Account(profile.DefaultAccountId);
            if (IsUsableBackend(definition, accRef)) return accRef;
        }

        // 3. Fallback to account service default
        if (accounts is not null && definition.Accounts is not null)
        {
            var preferredAcc = accounts.ResolvePreferredAccountId(definition, profile, project);
            if (!string.IsNullOrWhiteSpace(preferredAcc))
            {
                return AgentBackendRefs.Account(preferredAcc);
            }
        }

        return AgentBackendRefs.Account(AgentAccountRecord.DefaultId);
    }

    public string? DescribeBackend(AgentDefinition definition, string? backendRef)
    {
        if (string.IsNullOrWhiteSpace(backendRef)) return null;
        if (AgentBackendRefs.TryParseProvider(backendRef, out var providerId)
            || AgentBackendRefs.TryParseTunnel(backendRef, out providerId))
        {
            var provider = providers.Find(providerId);
            if (provider is null) return null;
            return AgentBackendRefs.TryParseTunnel(backendRef, out _)
                ? $"⚡ {provider.Name}"
                : provider.Name;
        }

        if (AgentBackendRefs.TryParseAccount(backendRef, out var accountId)
            && accounts is not null
            && definition.Accounts is not null)
        {
            var acc = accounts.GetAccounts(definition).FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.Ordinal));
            if (acc is not null && !acc.IsDefaultOrigin)
            {
                return acc.Label;
            }
        }

        return null;
    }

    public async Task<ResolvedAgentBackend> ResolveAsync(
        AgentDefinition definition,
        AgentProfile profile,
        Project? project = null,
        AgentLaunchOptions? launch = null,
        CancellationToken cancellationToken = default)
    {
        if (launch?.TunnelProvider is { } tunnelSpec
            && !string.IsNullOrWhiteSpace(launch.TunnelLoopbackBaseUrl)
            && definition.SupportsManagedProviders)
        {
            var chosenModel = !string.IsNullOrWhiteSpace(launch.Model) ? launch.Model : profile.Model;
            var materialized = materializer.MaterializeTunnel(definition, tunnelSpec, launch.TunnelLoopbackBaseUrl!, chosenModel);
            var syntheticProvider = new ModelProviderRecord
            {
                Id = "tunnel_" + tunnelSpec.GrantToken,
                Name = tunnelSpec.DisplayName,
                Protocol = tunnelSpec.Protocol,
                BaseUrl = launch.TunnelLoopbackBaseUrl!,
                Models = tunnelSpec.Models,
            };
            return new ResolvedAgentBackend(
                BackendRef: AgentBackendRefs.Tunnel(tunnelSpec.GrantToken),
                IsProvider: true,
                DisplayName: $"⚡ {tunnelSpec.DisplayName}",
                Account: null,
                Provider: syntheticProvider,
                Environment: materialized.Environment,
                UnsetEnvironment: materialized.UnsetEnvironment,
                ModelPrefix: materialized.ModelPrefix);
        }

        var chosenRef = PickEffectiveBackendRef(definition, profile, project, launch);

        if (AgentBackendRefs.TryParseProvider(chosenRef, out var providerId)
            && definition.SupportsManagedProviders)
        {
            var provider = providers.Find(providerId);
            if (provider is { Enabled: true } && definition.ProviderSupport!.SupportsProtocol(provider.Protocol))
            {
                string? secret;
                string? baseUrlOverride = null;
                if (CoWorkProviderIds.IsCoWorkProvider(provider.Id) && cowork is not null)
                {
                    var runtime = await cowork.ResolveRuntimeEndpointAsync(provider.Protocol, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("CoWork is not linked or authorization expired. Re-link CoWork in Octopus account settings.");

                    baseUrlOverride = runtime.BaseUrl;
                    secret = runtime.LocalApiKey;
                }
                else
                {
                    secret = await providers.ReadSecretAsync(provider.Id, cancellationToken).ConfigureAwait(false);
                }

                var chosenModel = !string.IsNullOrWhiteSpace(launch?.Model) ? launch.Model : profile.Model;
                var mat = materializer.Materialize(definition, provider, secret, chosenModel, baseUrlOverride: baseUrlOverride);
                return new ResolvedAgentBackend(
                    BackendRef: AgentBackendRefs.Provider(provider.Id),
                    IsProvider: true,
                    DisplayName: provider.Name,
                    Account: null,
                    Provider: provider,
                    Environment: mat.Environment,
                    UnsetEnvironment: mat.UnsetEnvironment,
                    ModelPrefix: mat.ModelPrefix);
            }
        }

        // Subscription account path (falls back safely whenever a provider was deleted/disabled).
        var accountLaunch = launch;
        if (AgentBackendRefs.TryParseAccount(chosenRef, out var chosenAccId)
            && !string.Equals(launch?.AccountId, chosenAccId, StringComparison.Ordinal))
        {
            accountLaunch = new AgentLaunchOptions
            {
                Model = launch?.Model,
                Effort = launch?.Effort,
                Fast = launch?.Fast,
                Permission = launch?.Permission,
                AccountId = chosenAccId,
                BackendRef = launch?.BackendRef,
                Surface = launch?.Surface,
                Prompt = launch?.Prompt,
                Resume = launch?.Resume ?? false,
                SessionId = launch?.SessionId,
                Env = launch?.Env,
            };
        }

        if (accounts is not null && definition.Accounts is not null)
        {
            var resolvedAcc = accounts.Resolve(definition, profile, project, accountLaunch);
            var accId = resolvedAcc.Account?.Id ?? AgentAccountRecord.DefaultId;
            return new ResolvedAgentBackend(
                BackendRef: AgentBackendRefs.Account(accId),
                IsProvider: false,
                DisplayName: resolvedAcc.Account?.Label ?? "Default",
                Account: resolvedAcc.Account,
                Provider: null,
                Environment: resolvedAcc.Environment,
                UnsetEnvironment: resolvedAcc.UnsetEnvironment);
        }

        return new ResolvedAgentBackend(
            BackendRef: AgentBackendRefs.Account(AgentAccountRecord.DefaultId),
            IsProvider: false,
            DisplayName: "Default",
            Account: null,
            Provider: null,
            Environment: null,
            UnsetEnvironment: null);
    }

    private string? PickEffectiveBackendRef(
        AgentDefinition definition,
        AgentProfile profile,
        Project? project,
        AgentLaunchOptions? launch)
    {
        if (!string.IsNullOrWhiteSpace(launch?.BackendRef) && IsUsableBackend(definition, launch.BackendRef))
        {
            return launch.BackendRef;
        }
        if (!string.IsNullOrWhiteSpace(launch?.AccountId))
        {
            var accRef = AgentBackendRefs.Account(launch.AccountId);
            if (IsUsableBackend(definition, accRef)) return accRef;
        }
        return ResolvePreferredBackendRef(definition, profile, project);
    }

    private bool IsUsableBackend(AgentDefinition definition, string? backendRef)
    {
        if (string.IsNullOrWhiteSpace(backendRef)) return false;

        if (AgentBackendRefs.TryParseProvider(backendRef, out var providerId))
        {
            if (definition.ProviderSupport is not { } cap) return false;
            var provider = providers.Find(providerId);
            return provider is { Enabled: true } && cap.SupportsProtocol(provider.Protocol);
        }

        if (AgentBackendRefs.TryParseAccount(backendRef, out var accountId))
        {
            if (string.Equals(accountId, AgentAccountRecord.DefaultId, StringComparison.Ordinal)) return true;
            if (accounts is null || definition.Accounts is null) return false;
            return accounts.GetAccounts(definition).Any(a => string.Equals(a.Id, accountId, StringComparison.Ordinal));
        }

        return false;
    }
}
