using System.Net.Http.Headers;
using System.Text.Json;
using Octopus.Agents.Providers;
using Octopus.Core.Agents;
using Octopus.Remote.Relay;

namespace Octopus.App.Remote;

/// <summary>
/// App-level coordinator for the Octopus account's 1:1 CoWork binding: tracks live wallet/team status via
/// Octopus Server, runs the in-process <see cref="CoWorkGatewayProxy"/> with automatic RFC 8693 delegated token
/// refresh, and syncs CoWork managed providers into <see cref="IModelProviderService"/> so any of the 61 coding
/// agents can immediately spend CoWork model quota.
/// </summary>
public sealed class CoWorkIntegrationService : ICoWorkProviderBridge, IDisposable
{
    private readonly RelayAccess _relay;
    private readonly IModelProviderService _providers;
    private readonly CoWorkGatewayProxy _proxy;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private bool _disposed;

    public CoWorkIntegrationService(RelayAccess relay, IModelProviderService providers)
    {
        _relay = relay;
        _providers = providers;
        var version = RelayClientInfo.AppVersion ?? "1.0";
        _proxy = new CoWorkGatewayProxy(FetchUpstreamTokenAsync, userAgent: $"Octopus/{version}");
        _relay.Changed += OnRelayChanged;
    }

    public event EventHandler? Changed;

    public CoWorkIntegrationStatus? Status { get; private set; }

    public IReadOnlyList<CoWorkTeamInfo> Teams { get; private set; } = [];

    public bool IsSignedIn => _relay.Account is not null;

    public bool IsBound => Status is { Bound: true };

    public bool IsActive => Status is { Bound: true, Status: "active" };

    public string? GetBindPageUrl()
    {
        if (!string.IsNullOrWhiteSpace(Status?.BindUrl))
        {
            return Status.BindUrl;
        }

        var baseUri = RelayAccountClient.ParseUrl(_relay.Url) ?? RelayAccountClient.ParseUrl(_relay.OfficialUrl);
        return baseUri is null ? null : new Uri(baseUri, "/account?view=settings").AbsoluteUri;
    }

    public async Task<CoWorkIntegrationStatus?> RefreshStatusAsync(bool autoSyncProviders = false, CancellationToken cancellationToken = default)
    {
        var endpoint = await _relay.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            Status = null;
            Teams = [];
            _proxy.InvalidateCachedToken();
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }

        var status = await RelayCoWorkClient.GetStatusAsync(endpoint.Url, endpoint.Token, cancellationToken).ConfigureAwait(false);
        Status = status;

        if (status is { Bound: true, Status: "active" })
        {
            var teams = await RelayCoWorkClient.GetTeamsAsync(endpoint.Url, endpoint.Token, cancellationToken).ConfigureAwait(false);
            Teams = teams?.Teams ?? [];
            if (autoSyncProviders && !_providers.ListProviders().Any(p => CoWorkProviderIds.IsCoWorkProvider(p.Id)))
            {
                await SyncProvidersAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            Teams = [];
            _proxy.InvalidateCachedToken();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return status;
    }

    public async Task<(bool Succeeded, string? Error)> SelectTeamAsync(string teamId, CancellationToken cancellationToken = default)
    {
        var endpoint = await _relay.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return (false, "not_signed_in");
        }

        var res = await RelayCoWorkClient.SelectTeamAsync(endpoint.Url, endpoint.Token, teamId, cancellationToken).ConfigureAwait(false);
        if (res.Succeeded)
        {
            _proxy.InvalidateCachedToken();
            await RefreshStatusAsync(autoSyncProviders: false, cancellationToken).ConfigureAwait(false);
        }

        return (res.Succeeded, res.Error);
    }

    public async Task<bool> UnbindAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = await _relay.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return false;
        }

        var ok = await RelayCoWorkClient.UnbindAsync(endpoint.Url, endpoint.Token, cancellationToken).ConfigureAwait(false);
        _proxy.InvalidateCachedToken();
        await RemoveManagedProvidersAsync(cancellationToken).ConfigureAwait(false);
        await RefreshStatusAsync(autoSyncProviders: false, cancellationToken).ConfigureAwait(false);
        return ok;
    }

    /// <summary>
    /// Ensures the local proxy is running, fetches live CoWork models, and registers/updates the CoWork managed
    /// providers (<c>cowork-anthropic</c>, <c>cowork-openai-responses</c>, <c>cowork-openai-chat</c>).
    /// </summary>
    public async Task<(bool Succeeded, int ModelCount, string? Error)> SyncProvidersAsync(CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var upstream = await FetchUpstreamTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
            if (upstream is null)
            {
                return (false, 0, Status?.Error ?? "cowork_not_bound");
            }

            _proxy.EnsureStarted();

            var discovered = await FetchCoWorkModelsAsync(upstream, cancellationToken).ConfigureAwait(false);
            var anthropicModels = FilterModels(discovered, isAnthropic: true);
            var openAiResponsesModels = FilterModels(discovered, isOpenAi: true);
            var openAiChatModels = discovered.Count > 0 ? discovered : DefaultChatFallbackModels();

            var now = DateTimeOffset.UtcNow;
            await _providers.SaveAsync(new ModelProviderRecord
            {
                Id = CoWorkProviderIds.Anthropic,
                Name = "CoWork (Anthropic)",
                Protocol = ProviderProtocol.Anthropic,
                BaseUrl = _proxy.AnthropicBaseUrl,
                AuthEnvPreference = "ANTHROPIC_AUTH_TOKEN",
                Enabled = true,
                IsolateConfigDir = true,
                AllowRemoteTunnel = true,
                Models = anthropicModels,
                ModelsSyncedAt = now,
            }, newApiKey: null, cancellationToken).ConfigureAwait(false);

            await _providers.SaveAsync(new ModelProviderRecord
            {
                Id = CoWorkProviderIds.OpenAIResponses,
                Name = "CoWork (OpenAI Responses)",
                Protocol = ProviderProtocol.OpenAIResponses,
                BaseUrl = _proxy.OpenAiBaseUrl,
                Enabled = true,
                IsolateConfigDir = true,
                AllowRemoteTunnel = true,
                Models = openAiResponsesModels,
                ModelsSyncedAt = now,
            }, newApiKey: null, cancellationToken).ConfigureAwait(false);

            await _providers.SaveAsync(new ModelProviderRecord
            {
                Id = CoWorkProviderIds.OpenAIChat,
                Name = "CoWork (OpenAI Chat)",
                Protocol = ProviderProtocol.OpenAIChat,
                BaseUrl = _proxy.OpenAiBaseUrl,
                Enabled = true,
                IsolateConfigDir = true,
                AllowRemoteTunnel = true,
                Models = openAiChatModels,
                ModelsSyncedAt = now,
            }, newApiKey: null, cancellationToken).ConfigureAwait(false);

            Changed?.Invoke(this, EventArgs.Empty);
            return (true, discovered.Count, null);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task<CoWorkRuntimeEndpoint?> ResolveRuntimeEndpointAsync(ProviderProtocol protocol, CancellationToken cancellationToken = default)
    {
        var upstream = await FetchUpstreamTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        if (upstream is null)
        {
            return null;
        }

        _proxy.EnsureStarted();
        var baseUrl = protocol == ProviderProtocol.Anthropic ? _proxy.AnthropicBaseUrl : _proxy.OpenAiBaseUrl;
        return new CoWorkRuntimeEndpoint(baseUrl, _proxy.LocalApiKey);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _relay.Changed -= OnRelayChanged;
        _proxy.Dispose();
        _syncGate.Dispose();
    }

    private void OnRelayChanged()
    {
        if (_disposed) return;
        if (_relay.Account is null)
        {
            Status = null;
            Teams = [];
            _proxy.InvalidateCachedToken();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _ = RefreshStatusAsync(autoSyncProviders: true);
        }
    }

    private async Task<CoWorkGatewayUpstream?> FetchUpstreamTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var endpoint = await _relay.GetAsync(fresh: forceRefresh, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return null;
        }

        var result = await RelayCoWorkClient.MintDelegatedTokenAsync(endpoint.Url, endpoint.Token, scope: null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Token is null)
        {
            if (result.Error is "cowork_reauth_required" or "cowork_not_bound")
            {
                _ = RefreshStatusAsync(autoSyncProviders: false, CancellationToken.None);
            }

            return null;
        }

        if (!Uri.TryCreate(result.Token.GatewayBaseUrl, UriKind.Absolute, out var gatewayUri))
        {
            return null;
        }

        return new CoWorkGatewayUpstream(gatewayUri, result.Token.AccessToken, result.Token.ExpiresAt);
    }

    private static async Task<List<ManagedModelSpec>> FetchCoWorkModelsAsync(CoWorkGatewayUpstream upstream, CancellationToken cancellationToken)
    {
        var list = new List<ManagedModelSpec>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(upstream.BaseUrl, "/v1/models"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstream.DelegatedAccessToken);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return list;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var displayName = item.TryGetProperty("display_name", out var dnEl) && dnEl.ValueKind == JsonValueKind.String ? dnEl.GetString() : null;
                int? ctxWindow = item.TryGetProperty("context_window", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number && ctxEl.TryGetInt32(out var cw) ? cw : null;

                list.Add(new ManagedModelSpec
                {
                    Id = id,
                    DisplayName = displayName,
                    Enabled = true,
                    Tier = InferTier(id),
                    ContextWindow = ctxWindow,
                });
            }
        }
        catch
        {
        }

        return list;
    }

    private static List<ManagedModelSpec> FilterModels(List<ManagedModelSpec> all, bool isAnthropic = false, bool isOpenAi = false)
    {
        if (all.Count == 0)
        {
            return isAnthropic ? DefaultAnthropicFallbackModels() : DefaultOpenAiFallbackModels();
        }

        var filtered = all.Where(m =>
        {
            var id = m.Id;
            if (isAnthropic)
            {
                return id.Contains("claude", StringComparison.OrdinalIgnoreCase);
            }
            if (isOpenAi)
            {
                return id.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("codex", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }).ToList();

        return filtered.Count > 0 ? filtered : all.Take(20).ToList();
    }

    private static ModelSlotTier InferTier(string modelId)
    {
        if (modelId.Contains("opus", StringComparison.OrdinalIgnoreCase) || modelId.Contains("pro", StringComparison.OrdinalIgnoreCase))
        {
            return ModelSlotTier.Flagship;
        }
        if (modelId.Contains("sonnet", StringComparison.OrdinalIgnoreCase))
        {
            return ModelSlotTier.Balanced;
        }
        if (modelId.Contains("haiku", StringComparison.OrdinalIgnoreCase) || modelId.Contains("mini", StringComparison.OrdinalIgnoreCase) || modelId.Contains("flash", StringComparison.OrdinalIgnoreCase))
        {
            return ModelSlotTier.Fast;
        }
        return ModelSlotTier.None;
    }

    private static List<ManagedModelSpec> DefaultAnthropicFallbackModels() =>
    [
        new() { Id = "claude-opus-4-6", DisplayName = "Claude Opus 4.6", Enabled = true, Tier = ModelSlotTier.Flagship },
        new() { Id = "claude-sonnet-4-6", DisplayName = "Claude Sonnet 4.6", Enabled = true, Tier = ModelSlotTier.Balanced },
        new() { Id = "claude-haiku-4-5", DisplayName = "Claude Haiku 4.5", Enabled = true, Tier = ModelSlotTier.Fast },
    ];

    private static List<ManagedModelSpec> DefaultOpenAiFallbackModels() =>
    [
        new() { Id = "gpt-5.4", DisplayName = "GPT-5.4", Enabled = true, Tier = ModelSlotTier.Flagship },
        new() { Id = "gpt-5.4-mini", DisplayName = "GPT-5.4 Mini", Enabled = true, Tier = ModelSlotTier.Fast },
    ];

    private static List<ManagedModelSpec> DefaultChatFallbackModels() =>
        [.. DefaultAnthropicFallbackModels(), .. DefaultOpenAiFallbackModels()];

    private async Task RemoveManagedProvidersAsync(CancellationToken cancellationToken)
    {
        foreach (var id in new[] { CoWorkProviderIds.Anthropic, CoWorkProviderIds.OpenAIResponses, CoWorkProviderIds.OpenAIChat })
        {
            await _providers.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }
}
