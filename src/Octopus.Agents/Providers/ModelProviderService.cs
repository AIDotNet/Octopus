using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Octopus.Core.Agents;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.Agents.Providers;

/// <summary>
/// Persists managed model providers in <see cref="IProviderStore"/> (<c>providers.json</c>) and stores API keys
/// exclusively in <see cref="ISecretStore"/> under <c>Octopus/providers</c>.
/// </summary>
public sealed class ModelProviderService : IModelProviderService
{
    public const string SecretServiceName = "Octopus/providers";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private static readonly Lazy<HttpClient> SharedRemoteHttp = new(() => new HttpClient(new HttpClientHandler
    {
        UseProxy = true,
    })
    { Timeout = RequestTimeout });

    private static readonly Lazy<HttpClient> SharedLoopbackHttp = new(() => new HttpClient(new HttpClientHandler
    {
        UseProxy = false,
    })
    { Timeout = RequestTimeout });

    private readonly IProviderStore _store;
    private readonly ISecretStore _secrets;
    private readonly HttpClient? _httpClient;
    private readonly Func<ICoWorkProviderBridge?>? _coworkAccessor;

    public ModelProviderService(
        IProviderStore store,
        ISecretStore secrets,
        HttpClient? httpClient = null,
        ICoWorkProviderBridge? cowork = null,
        Func<ICoWorkProviderBridge?>? coworkFactory = null)
    {
        _store = store;
        _secrets = secrets;
        _httpClient = httpClient;
        _coworkAccessor = coworkFactory ?? (cowork is null ? null : () => cowork);
        _store.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    private ICoWorkProviderBridge? Cowork => _coworkAccessor?.Invoke();

    public event EventHandler? Changed;

    public IReadOnlyList<ModelProviderRecord> ListProviders() =>
        _store.Current.Providers.Select(CloneProvider).ToList();

    public IReadOnlyList<ModelProviderRecord> ListCompatibleProviders(AgentDefinition definition, bool requireRemoteTunnel = false)
    {
        if (definition.ProviderSupport is not { Protocols.Count: > 0 } cap)
        {
            return [];
        }

        return _store.Current.Providers
            .Where(p => p.Enabled
                && (!requireRemoteTunnel || p.AllowRemoteTunnel)
                && cap.SupportsProtocol(p.Protocol))
            .Select(CloneProvider)
            .ToList();
    }

    public ModelProviderRecord? Find(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        var match = _store.Current.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId.Trim(), StringComparison.Ordinal));
        return match is null ? null : CloneProvider(match);
    }

    public IReadOnlyList<PinnedLaunchPreset> ListPinnedPresets() =>
        _store.Current.PinnedPresets.Select(ClonePreset).ToList();

    public async Task<ModelProviderRecord> SaveAsync(
        ModelProviderRecord record,
        string? newApiKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var copy = CloneProvider(record);
        if (string.IsNullOrWhiteSpace(copy.Id))
        {
            copy.Id = GenerateShortId();
            copy.CreatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            copy.Id = copy.Id.Trim();
        }

        if (string.IsNullOrWhiteSpace(copy.Name))
        {
            copy.Name = copy.Id;
        }

        if (newApiKey is not null)
        {
            var trimmedKey = newApiKey.Trim();
            if (trimmedKey.Length == 0)
            {
                await _secrets.DeleteAsync(SecretServiceName, copy.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _secrets.SetAsync(SecretServiceName, copy.Id, trimmedKey, cancellationToken).ConfigureAwait(false);
            }
        }

        _store.Update(doc =>
        {
            var idx = doc.Providers.FindIndex(p => string.Equals(p.Id, copy.Id, StringComparison.Ordinal));
            if (idx >= 0)
            {
                copy.CreatedAt = doc.Providers[idx].CreatedAt;
                doc.Providers[idx] = copy;
            }
            else
            {
                doc.Providers.Add(copy);
            }
        });

        return Find(copy.Id) ?? copy;
    }

    public async Task RemoveAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        var trimmed = providerId.Trim();
        await _secrets.DeleteAsync(SecretServiceName, trimmed, cancellationToken).ConfigureAwait(false);

        var providerRef = AgentBackendRefs.Provider(trimmed);
        var tunnelRef = AgentBackendRefs.Tunnel(trimmed);
        _store.Update(doc =>
        {
            doc.Providers.RemoveAll(p => string.Equals(p.Id, trimmed, StringComparison.Ordinal));
            doc.PinnedPresets.RemoveAll(p =>
                string.Equals(p.BackendRef, providerRef, StringComparison.Ordinal)
                || string.Equals(p.BackendRef, tunnelRef, StringComparison.Ordinal));
        });
    }

    public void SetEnabled(string providerId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        var trimmed = providerId.Trim();
        _store.Update(doc =>
        {
            var target = doc.Providers.FirstOrDefault(p => string.Equals(p.Id, trimmed, StringComparison.Ordinal));
            target?.Enabled = enabled;
        });
    }

    public async Task<bool> HasSecretAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var value = await ReadSecretAsync(providerId, cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrEmpty(value);
    }

    public async Task<string?> ReadSecretAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        return await _secrets.GetAsync(SecretServiceName, providerId.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderTestResult> TestConnectionAsync(
        ModelProviderRecord record,
        string? overrideApiKey = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var effective = record;
            string? apiKey;
            if (overrideApiKey is null && CoWorkProviderIds.IsCoWorkProvider(record.Id) && Cowork is { } cowork)
            {
                var runtime = await cowork.ResolveRuntimeEndpointAsync(record.Protocol, cancellationToken).ConfigureAwait(false);
                if (runtime is null)
                {
                    sw.Stop();
                    return new ProviderTestResult(false, (int)sw.ElapsedMilliseconds, "CoWork is not linked or authorization expired.");
                }

                effective = CloneProvider(record);
                effective.BaseUrl = runtime.BaseUrl;
                apiKey = runtime.LocalApiKey;
            }
            else
            {
                apiKey = overrideApiKey ?? await ReadSecretAsync(record.Id, cancellationToken).ConfigureAwait(false);
            }

            var (ok, models, error) = await QueryModelsEndpointAsync(effective, apiKey, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return new ProviderTestResult(ok, (int)sw.ElapsedMilliseconds, error, ok ? models.Count : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProviderTestResult(false, (int)sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ManagedModelSpec>> FetchRemoteModelsAsync(
        ModelProviderRecord record,
        string? overrideApiKey = null,
        CancellationToken cancellationToken = default)
    {
        var effective = record;
        string? apiKey;
        if (overrideApiKey is null && CoWorkProviderIds.IsCoWorkProvider(record.Id) && Cowork is { } cowork)
        {
            var runtime = await cowork.ResolveRuntimeEndpointAsync(record.Protocol, cancellationToken).ConfigureAwait(false);
            if (runtime is null)
            {
                return [];
            }

            effective = CloneProvider(record);
            effective.BaseUrl = runtime.BaseUrl;
            apiKey = runtime.LocalApiKey;
        }
        else
        {
            apiKey = overrideApiKey ?? await ReadSecretAsync(record.Id, cancellationToken).ConfigureAwait(false);
        }

        var (_, models, _) = await QueryModelsEndpointAsync(effective, apiKey, cancellationToken).ConfigureAwait(false);
        return models;
    }

    public PinnedLaunchPreset PinPreset(PinnedLaunchPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var copy = ClonePreset(preset);
        if (string.IsNullOrWhiteSpace(copy.Id))
        {
            copy.Id = GenerateShortId();
        }

        _store.Update(doc =>
        {
            var dup = doc.PinnedPresets.FirstOrDefault(p =>
                string.Equals(p.AgentId, copy.AgentId, StringComparison.Ordinal)
                && string.Equals(p.BackendRef, copy.BackendRef, StringComparison.Ordinal)
                && string.Equals(p.ModelId ?? "", copy.ModelId ?? "", StringComparison.Ordinal));
            if (dup is not null)
            {
                copy.Id = dup.Id;
                dup.Effort = copy.Effort;
                dup.CustomLabel = copy.CustomLabel;
            }
            else
            {
                doc.PinnedPresets.Add(copy);
            }
        });
        return copy;
    }

    public void UnpinPreset(string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return;
        var trimmed = presetId.Trim();
        _store.Update(doc => doc.PinnedPresets.RemoveAll(p => string.Equals(p.Id, trimmed, StringComparison.Ordinal)));
    }

    private async Task<(bool Ok, IReadOnlyList<ManagedModelSpec> Models, string? Error)> QueryModelsEndpointAsync(
        ModelProviderRecord record,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var rawBase = string.IsNullOrWhiteSpace(record.BaseUrl)
            ? (record.Protocol is ProviderProtocol.Gemini or ProviderProtocol.GeminiOfficialKey
                ? "https://generativelanguage.googleapis.com"
                : record.Protocol == ProviderProtocol.Anthropic
                    ? "https://api.anthropic.com"
                    : "https://api.openai.com/v1")
            : record.BaseUrl.Trim().TrimEnd('/');

        if (!Uri.TryCreate(rawBase, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            return (false, [], "Invalid Base URL (must start with http:// or https://).");
        }

        var urls = BuildModelUrls(record.Protocol, rawBase, apiKey);
        var client = _httpClient ?? (baseUri.IsLoopback ? SharedLoopbackHttp.Value : SharedRemoteHttp.Value);
        string? lastError = null;

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                ApplyAuthHeaders(req, record, apiKey);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(RequestTimeout);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})";
                    continue;
                }
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var models = ParseModels(record.Protocol, body);
                return (true, models, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        return (false, [], lastError ?? "Unable to reach provider endpoint.");
    }

    private static IReadOnlyList<string> BuildModelUrls(ProviderProtocol protocol, string baseUrl, string? apiKey)
    {
        if (protocol is ProviderProtocol.Gemini or ProviderProtocol.GeminiOfficialKey)
        {
            var suffix = !string.IsNullOrWhiteSpace(apiKey) ? $"?key={Uri.EscapeDataString(apiKey)}" : "";
            return baseUrl.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)
                ? [$"{baseUrl}/models{suffix}"]
                : [$"{baseUrl}/v1beta/models{suffix}", $"{baseUrl}/models{suffix}"];
        }

        return baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? [$"{baseUrl}/models"]
            : [$"{baseUrl}/v1/models", $"{baseUrl}/models"];
    }

    private static void ApplyAuthHeaders(HttpRequestMessage req, ModelProviderRecord record, string? apiKey)
    {
        foreach (var (k, v) in record.Headers)
        {
            if (!string.IsNullOrWhiteSpace(k))
            {
                req.Headers.TryAddWithoutValidation(k, v);
            }
        }

        if (record.Protocol == ProviderProtocol.Anthropic)
        {
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }
        else if (record.Protocol is ProviderProtocol.OpenAIResponses or ProviderProtocol.OpenAIChat)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }
        else if (record.Protocol is ProviderProtocol.Gemini or ProviderProtocol.GeminiOfficialKey)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
            }
        }
    }

    public static IReadOnlyList<ManagedModelSpec> ParseModels(ProviderProtocol protocol, string json)
    {
        if (protocol is ProviderProtocol.Gemini or ProviderProtocol.GeminiOfficialKey)
        {
            var gemini = ParseGeminiModels(json);
            if (gemini.Count > 0) return gemini;
        }

        var options = ThirdPartyModelDiscovery.ParseGatewayModelsJson(json);
        return options.Select(o => new ManagedModelSpec
        {
            Id = o.Id,
            DisplayName = o.Label,
            Enabled = true,
        }).ToList();
    }

    private static IReadOnlyList<ManagedModelSpec> ParseGeminiModels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("models", out var modelsEl)
                || modelsEl.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<ManagedModelSpec>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in modelsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var rawName = item.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(rawName)) continue;
                var id = rawName.StartsWith("models/", StringComparison.Ordinal) ? rawName["models/".Length..] : rawName;
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                var display = item.TryGetProperty("displayName", out var dEl) && dEl.ValueKind == JsonValueKind.String ? dEl.GetString() : null;
                list.Add(new ManagedModelSpec
                {
                    Id = id,
                    DisplayName = string.IsNullOrWhiteSpace(display) ? null : display,
                    Enabled = true,
                });
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string GenerateShortId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private static ModelProviderRecord CloneProvider(ModelProviderRecord p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Protocol = p.Protocol,
        BaseUrl = p.BaseUrl,
        AuthEnvPreference = p.AuthEnvPreference,
        Headers = new Dictionary<string, string>(p.Headers, StringComparer.Ordinal),
        Enabled = p.Enabled,
        IsolateConfigDir = p.IsolateConfigDir,
        AllowRemoteTunnel = p.AllowRemoteTunnel,
        CreatedAt = p.CreatedAt,
        ModelsSyncedAt = p.ModelsSyncedAt,
        Models = p.Models.Select(m => new ManagedModelSpec
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            Enabled = m.Enabled,
            Tier = m.Tier,
            SupportedEfforts = m.SupportedEfforts is { Count: > 0 } ? [.. m.SupportedEfforts] : null,
            ContextWindow = m.ContextWindow,
        }).ToList(),
    };

    private static PinnedLaunchPreset ClonePreset(PinnedLaunchPreset p) => new()
    {
        Id = p.Id,
        AgentId = p.AgentId,
        BackendRef = p.BackendRef,
        ModelId = p.ModelId,
        Effort = p.Effort,
        CustomLabel = p.CustomLabel,
    };
}
