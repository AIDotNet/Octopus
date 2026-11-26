using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Octopus.Agents.Signals;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents;

/// <summary>
/// Discovers models for Codex CLI (custom gateways like Magpie / OneAPI / OpenRouter, <c>config.toml</c>,
/// <c>models_cache.json</c>, <c>model_catalog_json</c>, and <c>codex app-server</c> <c>model/list</c>) and
/// Anthropic-compatible third-party gateways configured for Claude Code.
/// Pure <see cref="JsonDocument"/> + <see cref="MiniToml"/> — zero reflection, NativeAOT safe.
/// </summary>
public static class ThirdPartyModelDiscovery
{
    private static readonly TimeSpan GatewayTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AppServerTimeout = TimeSpan.FromSeconds(10);

    private static readonly Lazy<HttpClient> SharedRemoteHttp = new(() => new HttpClient(new HttpClientHandler
    {
        UseProxy = true,
    })
    { Timeout = GatewayTimeout });

    private static readonly Lazy<HttpClient> SharedLoopbackHttp = new(() => new HttpClient(new HttpClientHandler
    {
        UseProxy = false,
    })
    { Timeout = GatewayTimeout });

    /// <summary>
    /// Discovers Codex models from: (1) <c>model_catalog_json</c>, (2) live custom gateway <c>GET {baseUrl}/models</c>,
    /// (3) <c>{CODEX_HOME}/models_cache.json</c>, (4) <c>codex app-server</c> <c>model/list</c>, plus any models
    /// explicitly named in <c>{CODEX_HOME}/config.toml</c>.
    /// </summary>
    public static async Task<IReadOnlyList<AgentOption>> DiscoverCodexAsync(
        string? executable,
        IReadOnlyList<string> appServerArgs,
        IReadOnlyDictionary<string, string> env,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var home = ResolveHome(env);
        var codexHome = ResolveDirectory(GetEnv(env, "CODEX_HOME"), Path.Combine(home, ".codex"), home);
        var config = ReadCodexConfig(Path.Combine(codexHome, "config.toml"), env, home);

        var discovered = new List<AgentOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AppendUnique(IEnumerable<AgentOption> items)
        {
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.Id) && seen.Add(item.Id))
                {
                    discovered.Add(item);
                }
            }
        }

        // 1. Explicit model_catalog_json in config.toml
        if (!string.IsNullOrWhiteSpace(config.ModelCatalogJson) && File.Exists(config.ModelCatalogJson))
        {
            AppendUnique(ReadCodexCatalogFile(config.ModelCatalogJson));
        }

        // 2. Live custom gateway GET {baseUrl}/models (when a non-official OpenAI base URL or custom provider is configured)
        if (!string.IsNullOrWhiteSpace(config.CustomBaseUrl))
        {
            var token = config.BearerToken ?? ReadCodexAuthToken(Path.Combine(codexHome, "auth.json"));
            var gatewayModels = await FetchCodexGatewayModelsAsync(
                config.CustomBaseUrl!,
                token,
                config.Headers,
                httpClient,
                cancellationToken).ConfigureAwait(false);
            AppendUnique(gatewayModels);
        }

        // 3. Cached models_cache.json (populated by Codex CLI, including Magpie /backend-api/codex/models responses)
        if (discovered.Count == 0)
        {
            var cachePath = Path.Combine(codexHome, "models_cache.json");
            if (File.Exists(cachePath))
            {
                AppendUnique(ReadCodexCatalogFile(cachePath));
            }
        }

        // 4. Fallback to live `codex app-server` `model/list` when no catalog/gateway/cache was available
        if (discovered.Count == 0 && !string.IsNullOrWhiteSpace(executable) && appServerArgs.Count > 0)
        {
            var rpcModels = await QueryCodexAppServerModelsAsync(executable, appServerArgs, env, cancellationToken)
                .ConfigureAwait(false);
            AppendUnique(rpcModels);
        }

        // 5. Always include explicitly configured model(s) from config.toml (top-level model first, profile models after)
        if (!string.IsNullOrWhiteSpace(config.ConfiguredModel) && seen.Add(config.ConfiguredModel))
        {
            discovered.Insert(0, new AgentOption { Id = config.ConfiguredModel });
        }
        foreach (var profileModel in config.ProfileModels)
        {
            if (!string.IsNullOrWhiteSpace(profileModel) && seen.Add(profileModel))
            {
                discovered.Add(new AgentOption { Id = profileModel });
            }
        }

        return discovered;
    }

    /// <summary>
    /// Discovers third-party Anthropic-compatible models when <c>ANTHROPIC_BASE_URL</c> (in environment or
    /// <c>{CLAUDE_CONFIG_DIR}/settings.json</c>) points to a non-official endpoint.
    /// Returns empty immediately for official Anthropic setups.
    /// </summary>
    public static async Task<IReadOnlyList<AgentOption>> DiscoverAnthropicAsync(
        IReadOnlyDictionary<string, string> env,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var home = ResolveHome(env);
        var claudeDir = ResolveDirectory(GetEnv(env, "CLAUDE_CONFIG_DIR"), Path.Combine(home, ".claude"), home);
        var settings = ReadClaudeSettings(Path.Combine(claudeDir, "settings.json"));

        var baseUrl = FirstNonEmpty(GetEnv(env, "ANTHROPIC_BASE_URL"), settings.BaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl) || IsOfficialAnthropicUrl(baseUrl))
        {
            return [];
        }

        var apiKey = FirstNonEmpty(GetEnv(env, "ANTHROPIC_API_KEY"), settings.ApiKey);
        var authToken = FirstNonEmpty(GetEnv(env, "ANTHROPIC_AUTH_TOKEN"), settings.AuthToken);

        var discovered = new List<AgentOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(AgentOption option)
        {
            if (!string.IsNullOrWhiteSpace(option.Id) && seen.Add(option.Id))
            {
                discovered.Add(option);
            }
        }

        var gatewayModels = await FetchAnthropicGatewayModelsAsync(baseUrl, apiKey, authToken, httpClient, cancellationToken)
            .ConfigureAwait(false);
        foreach (var m in gatewayModels) Add(m);

        var configuredModels = new[]
        {
            GetEnv(env, "ANTHROPIC_MODEL"),
            settings.Model,
            GetEnv(env, "ANTHROPIC_DEFAULT_OPUS_MODEL"),
            settings.DefaultOpusModel,
            GetEnv(env, "ANTHROPIC_DEFAULT_SONNET_MODEL"),
            settings.DefaultSonnetModel,
            GetEnv(env, "ANTHROPIC_DEFAULT_HAIKU_MODEL"),
            settings.DefaultHaikuModel,
        };
        foreach (var modelId in configuredModels)
        {
            if (!string.IsNullOrWhiteSpace(modelId) && seen.Add(modelId.Trim()))
            {
                discovered.Insert(0, new AgentOption { Id = modelId.Trim() });
            }
        }

        return discovered;
    }

    /// <summary>Parses Codex's <c>models_cache.json</c> / <c>model_catalog_json</c> / <c>/backend-api/codex/models</c> JSON.</summary>
    public static IReadOnlyList<AgentOption> ParseCodexCatalogJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseModelsElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Parses OpenAI (<c>{"data":[...]}</c>) or Codex (<c>{"models":[...]}</c>) model listing JSON.</summary>
    public static IReadOnlyList<AgentOption> ParseGatewayModelsJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseModelsElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Parses a Codex <c>app-server</c> <c>model/list</c> JSON-RPC <c>result</c> object.</summary>
    public static IReadOnlyList<AgentOption> ParseCodexAppServerResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<AgentOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("hidden", out var hiddenEl) && hiddenEl.ValueKind == JsonValueKind.True) continue;
            var id = TryStr(item, "id") ?? TryStr(item, "model");
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            var label = TryStr(item, "displayName") ?? TryStr(item, "name");
            var desc = TryStr(item, "description");
            list.Add(new AgentOption
            {
                Id = id,
                Label = !string.IsNullOrWhiteSpace(label) ? label : null,
                Note = BuildOptionNote(id, label, desc),
            });
        }
        return list;
    }

    private static IReadOnlyList<AgentOption> ParseModelsElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return [];

        // 1. Codex catalog / Magpie shape: { "models": [{ "slug", "display_name", "description", "visibility", "priority" }] }
        if (root.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array)
        {
            var entries = new List<(int Priority, int Order, AgentOption Option)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in modelsEl.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object) continue;
                var visibility = TryStr(item, "visibility");
                if (string.Equals(visibility, "hide", StringComparison.OrdinalIgnoreCase)) continue;
                var id = TryStr(item, "slug") ?? TryStr(item, "id") ?? TryStr(item, "model");
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                var label = TryStr(item, "display_name") ?? TryStr(item, "displayName") ?? TryStr(item, "name");
                var desc = TryStr(item, "description");
                var priority = item.TryGetProperty("priority", out var pEl) && pEl.TryGetInt32(out var p) ? p : int.MaxValue;
                entries.Add((priority, index, new AgentOption
                {
                    Id = id,
                    Label = !string.IsNullOrWhiteSpace(label) ? label : null,
                    Note = BuildOptionNote(id, label, desc),
                }));
            }
            entries.Sort((a, b) =>
            {
                var cmp = a.Priority.CompareTo(b.Priority);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });
            return entries.Select(e => e.Option).ToList();
        }

        // 2. OpenAI / Anthropic standard shape: { "data": [{ "id", "display_name"|"name", "owned_by" }] }
        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<AgentOption>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in dataEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("hidden", out var hiddenEl) && hiddenEl.ValueKind == JsonValueKind.True) continue;
                var id = TryStr(item, "id") ?? TryStr(item, "model") ?? TryStr(item, "slug");
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                var label = TryStr(item, "display_name") ?? TryStr(item, "displayName") ?? TryStr(item, "name");
                var ownedBy = TryStr(item, "owned_by");
                var note = !string.IsNullOrWhiteSpace(ownedBy)
                    && !string.Equals(ownedBy, "system", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ownedBy, "organization-owner", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ownedBy, id, StringComparison.OrdinalIgnoreCase)
                        ? ownedBy
                        : BuildOptionNote(id, label, TryStr(item, "description"));
                list.Add(new AgentOption
                {
                    Id = id,
                    Label = !string.IsNullOrWhiteSpace(label) ? label : null,
                    Note = note,
                });
            }
            return list;
        }

        return [];
    }

    private static string? BuildOptionNote(string id, string? label, string? description)
    {
        if (!string.IsNullOrWhiteSpace(label) && !string.Equals(label, id, StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }
        if (!string.IsNullOrWhiteSpace(description) && !string.Equals(description, label ?? id, StringComparison.OrdinalIgnoreCase))
        {
            return description;
        }
        return null;
    }

    private static IReadOnlyList<AgentOption> ReadCodexCatalogFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return ParseCodexCatalogJson(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<AgentOption>> FetchCodexGatewayModelsAsync(
        string baseUrl,
        string? bearerToken,
        IReadOnlyDictionary<string, string> extraHeaders,
        HttpClient? httpClient,
        CancellationToken cancellationToken)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            return [];
        }

        var candidateUrls = new List<string>(2);
        if (trimmed.Contains("/backend-api/", StringComparison.OrdinalIgnoreCase))
        {
            candidateUrls.Add($"{trimmed}/models?client_version=0.171.0");
            candidateUrls.Add($"{trimmed}/models");
        }
        else
        {
            candidateUrls.Add($"{trimmed}/models");
        }

        var client = httpClient ?? (baseUri.IsLoopback ? SharedLoopbackHttp.Value : SharedRemoteHttp.Value);
        foreach (var url in candidateUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (!string.IsNullOrWhiteSpace(bearerToken))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                }
                foreach (var (k, v) in extraHeaders)
                {
                    req.Headers.TryAddWithoutValidation(k, v);
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(GatewayTimeout);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var parsed = ParseGatewayModelsJson(body);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Best-effort gateway query; fall back to local cache / app-server.
            }
        }
        return [];
    }

    private static async Task<IReadOnlyList<AgentOption>> FetchAnthropicGatewayModelsAsync(
        string baseUrl,
        string? apiKey,
        string? authToken,
        HttpClient? httpClient,
        CancellationToken cancellationToken)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            return [];
        }

        var urls = trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? new[] { $"{trimmed}/models" }
            : new[] { $"{trimmed}/v1/models", $"{trimmed}/models" };

        var client = httpClient ?? (baseUri.IsLoopback ? SharedLoopbackHttp.Value : SharedRemoteHttp.Value);
        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                }
                var bearer = !string.IsNullOrWhiteSpace(authToken) ? authToken : apiKey;
                if (!string.IsNullOrWhiteSpace(bearer))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(GatewayTimeout);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var parsed = ParseGatewayModelsJson(body);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Best-effort gateway query.
            }
        }
        return [];
    }

    private static async Task<IReadOnlyList<AgentOption>> QueryCodexAppServerModelsAsync(
        string executable,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env,
        CancellationToken cancellationToken)
    {
        var psi = ProcessCommand.Create(executable, args);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.Environment.Clear();
        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) return [];
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            return [];
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { }
            }
            catch { }
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(AppServerTimeout);
        try
        {
            await proc.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"octopus","version":"1.0.0"}}}""")
                .ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(cts.Token).ConfigureAwait(false);

            var initResp = await ReadJsonRpcResponseAsync(proc.StandardOutput, 1, cts.Token).ConfigureAwait(false);
            if (initResp is null || initResp.Value.TryGetProperty("error", out _))
            {
                return [];
            }

            await proc.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":2,"method":"model/list","params":{}}""")
                .ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(cts.Token).ConfigureAwait(false);

            var listResp = await ReadJsonRpcResponseAsync(proc.StandardOutput, 2, cts.Token).ConfigureAwait(false);
            if (listResp is { ValueKind: JsonValueKind.Object } resp
                && resp.TryGetProperty("result", out var result))
            {
                return ParseCodexAppServerResult(result);
            }
            return [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }
    }

    private static async Task<JsonElement?> ReadJsonRpcResponseAsync(StreamReader reader, long targetId, CancellationToken ct)
    {
        for (var i = 0; i < 200; i++)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return null;
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && !root.TryGetProperty("method", out _)
                    && root.TryGetProperty("id", out var idEl)
                    && idEl.ValueKind == JsonValueKind.Number
                    && idEl.TryGetInt64(out var id)
                    && id == targetId)
                {
                    return root.Clone();
                }
            }
            catch (JsonException)
            {
                // Ignore non-JSON log lines.
            }
        }
        return null;
    }

    internal sealed record CodexConfigSummary(
        string? ConfiguredModel,
        IReadOnlyList<string> ProfileModels,
        string? CustomBaseUrl,
        string? BearerToken,
        IReadOnlyDictionary<string, string> Headers,
        string? ModelCatalogJson);

    internal static CodexConfigSummary ReadCodexConfig(string configPath, IReadOnlyDictionary<string, string> env, string home)
    {
        Dictionary<string, object?> toml = [];
        if (File.Exists(configPath))
        {
            try
            {
                toml = MiniToml.Parse(File.ReadAllText(configPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        string? TomlStr(string key) => toml.TryGetValue(key, out var val) && val is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

        var configuredModel = TomlStr("model");
        var profileModels = new List<string>();
        foreach (var (key, val) in toml)
        {
            if (key.StartsWith("profiles.", StringComparison.Ordinal)
                && key.EndsWith(".model", StringComparison.Ordinal)
                && val is string pm
                && !string.IsNullOrWhiteSpace(pm))
            {
                profileModels.Add(pm.Trim());
            }
        }

        var providerId = TomlStr("model_provider");
        string? providerBaseUrl = null;
        string? providerEnvKey = null;
        string? providerBearer = null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var prefix = $"model_providers.{providerId}.";
            providerBaseUrl = TomlStr(prefix + "base_url");
            providerEnvKey = TomlStr(prefix + "env_key");
            providerBearer = TomlStr(prefix + "experimental_bearer_token");
            var headerPrefix = prefix + "http_headers.";
            var envHeaderPrefix = prefix + "env_http_headers.";
            foreach (var (key, val) in toml)
            {
                if (key.StartsWith(headerPrefix, StringComparison.Ordinal) && val is string hv && hv.Length > 0)
                {
                    headers[key[headerPrefix.Length..]] = hv;
                }
                else if (key.StartsWith(envHeaderPrefix, StringComparison.Ordinal) && val is string envName
                    && GetEnv(env, envName) is { Length: > 0 } resolvedHeader)
                {
                    headers[key[envHeaderPrefix.Length..]] = resolvedHeader;
                }
            }
        }

        var envOpenAiBaseUrl = GetEnv(env, "OPENAI_BASE_URL");
        var tomlOpenAiBaseUrl = TomlStr("openai_base_url");
        var candidateBaseUrl = FirstNonEmpty(
            providerBaseUrl is not null && !IsOfficialOpenAiUrl(providerBaseUrl) ? providerBaseUrl : null,
            envOpenAiBaseUrl is not null && !IsOfficialOpenAiUrl(envOpenAiBaseUrl) ? envOpenAiBaseUrl : null,
            tomlOpenAiBaseUrl is not null && !IsOfficialOpenAiUrl(tomlOpenAiBaseUrl) ? tomlOpenAiBaseUrl : null);

        var bearerToken = FirstNonEmpty(
            providerBearer,
            !string.IsNullOrWhiteSpace(providerEnvKey) ? GetEnv(env, providerEnvKey) : null,
            GetEnv(env, "OPENAI_API_KEY"),
            GetEnv(env, "CODEX_API_KEY"));

        var catalogJson = TomlStr("model_catalog_json");
        if (!string.IsNullOrWhiteSpace(catalogJson))
        {
            catalogJson = SignalAccounts.NormalizeDirectory(catalogJson, home);
        }

        return new CodexConfigSummary(configuredModel, profileModels, candidateBaseUrl, bearerToken, headers, catalogJson);
    }

    private static string? ReadCodexAuthToken(string authPath)
    {
        if (!File.Exists(authPath)) return null;
        try
        {
            using var stream = new FileStream(authPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (TryStr(root, "OPENAI_API_KEY") is { Length: > 0 } apiKey) return apiKey;
            if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object
                && TryStr(tokens, "access_token") is { Length: > 0 } accessToken)
            {
                return accessToken;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record ClaudeSettingsSummary(
        string? BaseUrl,
        string? ApiKey,
        string? AuthToken,
        string? Model,
        string? DefaultOpusModel,
        string? DefaultSonnetModel,
        string? DefaultHaikuModel);

    private static ClaudeSettingsSummary ReadClaudeSettings(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return new(null, null, null, null, null, null, null);
        try
        {
            using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, null, null, null, null, null, null);

            string? baseUrl = null, apiKey = null, authToken = null, envModel = null, opus = null, sonnet = null, haiku = null;
            if (root.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
            {
                baseUrl = TryStr(envEl, "ANTHROPIC_BASE_URL");
                apiKey = TryStr(envEl, "ANTHROPIC_API_KEY");
                authToken = TryStr(envEl, "ANTHROPIC_AUTH_TOKEN");
                envModel = TryStr(envEl, "ANTHROPIC_MODEL");
                opus = TryStr(envEl, "ANTHROPIC_DEFAULT_OPUS_MODEL");
                sonnet = TryStr(envEl, "ANTHROPIC_DEFAULT_SONNET_MODEL");
                haiku = TryStr(envEl, "ANTHROPIC_DEFAULT_HAIKU_MODEL");
            }
            var model = FirstNonEmpty(envModel, TryStr(root, "model"));
            return new(baseUrl, apiKey, authToken, model, opus, sonnet, haiku);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(null, null, null, null, null, null, null);
        }
    }

    private static bool IsOfficialOpenAiUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("chat.openai.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOfficialAnthropicUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveHome(IReadOnlyDictionary<string, string> env) =>
        FirstNonEmpty(GetEnv(env, "HOME"), GetEnv(env, "USERPROFILE"))
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ResolveDirectory(string? configured, string fallback, string home) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : SignalAccounts.NormalizeDirectory(configured, home);

    private static string? GetEnv(IReadOnlyDictionary<string, string> env, string name)
    {
        if (env.TryGetValue(name, out var exact) && !string.IsNullOrWhiteSpace(exact)) return exact.Trim();
        foreach (var (k, v) in env)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }
        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    private static string? TryStr(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
