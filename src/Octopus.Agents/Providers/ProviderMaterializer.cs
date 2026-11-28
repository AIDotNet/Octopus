using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents.Providers;

/// <summary>
/// Compiles a <see cref="ModelProviderRecord"/> (or a remote tunnel loopback endpoint) into isolated per-agent
/// environment variables and overlay configuration files without mutating the user's native CLI directories
/// (design/20-模型提供商与模型托管.md §4.2).
/// </summary>
public sealed class ProviderMaterializer
{
    public const string ProviderKeyEnvName = "OCTOPUS_PROVIDER_KEY";
    public const string OpenCodeConfigContentEnv = "OPENCODE_CONFIG_CONTENT";
    public const string OpenCodeProviderId = "octopus";

    private readonly IAppPaths _paths;
    private readonly string _home;

    public ProviderMaterializer(IAppPaths paths, string? home = null)
    {
        _paths = paths;
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public ProviderMaterialization Materialize(
        AgentDefinition definition,
        ModelProviderRecord provider,
        string? apiKey,
        string? selectedModelId = null,
        string? baseUrlOverride = null,
        string? dirKeyOverride = null)
    {
        var cap = definition.ProviderSupport
            ?? throw new InvalidOperationException($"Agent '{definition.Id}' does not declare providerSupport.");

        var effectiveBaseUrl = string.IsNullOrWhiteSpace(baseUrlOverride)
            ? provider.BaseUrl.Trim().TrimEnd('/')
            : baseUrlOverride.Trim().TrimEnd('/');
        var dirKey = SanitizeKey(dirKeyOverride ?? provider.Id);

        return cap.Strategy switch
        {
            ProviderMaterializeStrategies.CodexHomeOverlay =>
                MaterializeCodexHome(definition, cap, provider, apiKey, effectiveBaseUrl, dirKey),
            ProviderMaterializeStrategies.OpenCodeConfigOverlay =>
                MaterializeOpenCodeOverlay(cap, provider, apiKey, effectiveBaseUrl),
            _ =>
                MaterializeEnv(definition, cap, provider, apiKey, selectedModelId, effectiveBaseUrl, dirKey),
        };
    }

    /// <summary>Materializes a remote controller API tunnel descriptor pointing at the remote host's loopback proxy.</summary>
    public ProviderMaterialization MaterializeTunnel(
        AgentDefinition definition,
        RemoteTunnelProviderSpec spec,
        string loopbackBaseUrl,
        string? selectedModelId = null)
    {
        var synthetic = new ModelProviderRecord
        {
            Id = "tunnel_" + SanitizeKey(spec.GrantToken),
            Name = spec.DisplayName,
            Protocol = spec.Protocol,
            BaseUrl = loopbackBaseUrl,
            AuthEnvPreference = spec.AuthEnvPreference,
            IsolateConfigDir = spec.IsolateConfigDir,
            Models = spec.Models,
        };
        var fakeKey = "oct-tunnel-" + spec.GrantToken;
        return Materialize(definition, synthetic, fakeKey, selectedModelId, loopbackBaseUrl, synthetic.Id);
    }

    private ProviderMaterialization MaterializeEnv(
        AgentDefinition definition,
        AgentProviderCapability cap,
        ModelProviderRecord provider,
        string? apiKey,
        string? selectedModelId,
        string effectiveBaseUrl,
        string dirKey)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var unsets = new List<string>(cap.UnsetEnv);
        var protoKey = ProviderProtocolNames.ToConfigName(provider.Protocol);

        if (cap.ProtocolExtraEnv is not null)
        {
            foreach (var (k, v) in cap.ProtocolExtraEnv)
            {
                if (string.Equals(k, protoKey, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (ek, ev) in v) env[ek] = ev;
                }
            }
        }

        // Base URL (never set custom URL for GeminiOfficialKey unless explicitly overridden by a tunnel).
        var baseUrlEnv = LookupProtocolMap(cap.ProtocolBaseUrlEnvs, protoKey) ?? cap.BaseUrlEnv;
        if (!string.IsNullOrWhiteSpace(baseUrlEnv)
            && !string.IsNullOrWhiteSpace(effectiveBaseUrl)
            && (provider.Protocol != ProviderProtocol.GeminiOfficialKey || effectiveBaseUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)))
        {
            env[baseUrlEnv] = effectiveBaseUrl;
        }

        // API key env var selection (respect provider.AuthEnvPreference when the agent supports AltApiKeyEnv, e.g. Claude).
        var apiKeyEnv = LookupProtocolMap(cap.ProtocolApiKeyEnvs, protoKey) ?? cap.ApiKeyEnv;
        if (!string.IsNullOrWhiteSpace(provider.AuthEnvPreference)
            && (string.Equals(provider.AuthEnvPreference, cap.ApiKeyEnv, StringComparison.Ordinal)
                || string.Equals(provider.AuthEnvPreference, cap.AltApiKeyEnv, StringComparison.Ordinal)))
        {
            apiKeyEnv = provider.AuthEnvPreference;
        }

        if (!string.IsNullOrWhiteSpace(apiKeyEnv) && !string.IsNullOrEmpty(apiKey))
        {
            env[apiKeyEnv] = apiKey;
        }

        // Model env & tier slot mappings (e.g. Claude ANTHROPIC_DEFAULT_{OPUS,SONNET,HAIKU}_MODEL).
        var enabledModels = provider.Models.Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Id)).ToList();
        var fallbackModel = !string.IsNullOrWhiteSpace(selectedModelId)
            ? selectedModelId.Trim()
            : enabledModels.FirstOrDefault()?.Id;

        if (!string.IsNullOrWhiteSpace(cap.ModelEnv) && !string.IsNullOrWhiteSpace(fallbackModel))
        {
            env[cap.ModelEnv] = fallbackModel;
        }

        if (cap.TierModelEnvs is { Count: > 0 } && enabledModels.Count > 0)
        {
            var flagship = enabledModels.FirstOrDefault(m => m.Tier == ModelSlotTier.Flagship)?.Id ?? fallbackModel;
            var balanced = enabledModels.FirstOrDefault(m => m.Tier == ModelSlotTier.Balanced)?.Id ?? flagship;
            var fast = enabledModels.FirstOrDefault(m => m.Tier == ModelSlotTier.Fast)?.Id ?? balanced;

            foreach (var (tierKey, envName) in cap.TierModelEnvs)
            {
                if (string.IsNullOrWhiteSpace(envName)) continue;
                var modelForTier = tierKey.ToLowerInvariant() switch
                {
                    "flagship" => flagship,
                    "balanced" => balanced,
                    "fast" => fast,
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(modelForTier))
                {
                    env[envName] = modelForTier;
                }
            }
        }

        // Isolate config dir (e.g. CLAUDE_CONFIG_DIR) so official OAuth sessions never conflict with API provider launches.
        string? isolatedDir = null;
        if (provider.IsolateConfigDir && !string.IsNullOrWhiteSpace(cap.IsolateConfigDirEnv))
        {
            isolatedDir = Path.Combine(_paths.DataDirectory, "provider-homes", definition.Id, dirKey);
            EnsurePrivateDirectory(isolatedDir);
            if (string.Equals(definition.Id, "claude", StringComparison.OrdinalIgnoreCase))
            {
                SyncClaudeNonSecretFiles(isolatedDir);
            }
            env[cap.IsolateConfigDirEnv] = isolatedDir;
        }

        // Never unset keys we explicitly injected.
        unsets.RemoveAll(k => env.ContainsKey(k));
        var modelPrefix = LookupProtocolMap(cap.ProtocolModelPrefixes, protoKey) ?? cap.ModelPrefix;
        return new ProviderMaterialization(env, unsets, modelPrefix, isolatedDir);
    }

    private ProviderMaterialization MaterializeCodexHome(
        AgentDefinition definition,
        AgentProviderCapability cap,
        ModelProviderRecord provider,
        string? apiKey,
        string effectiveBaseUrl,
        string dirKey)
    {
        var isolatedDir = Path.Combine(_paths.DataDirectory, "provider-homes", definition.Id, dirKey);
        EnsurePrivateDirectory(isolatedDir);

        var catalogPath = Path.Combine(isolatedDir, "catalog.json");
        var configPath = Path.Combine(isolatedDir, "config.toml");

        WriteCodexCatalog(catalogPath, provider);
        WriteCodexConfig(configPath, provider, effectiveBaseUrl, catalogPath);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CODEX_HOME"] = isolatedDir,
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            env[ProviderKeyEnvName] = apiKey;
        }

        var unsets = cap.UnsetEnv.Where(k => !env.ContainsKey(k)).ToList();
        return new ProviderMaterialization(env, unsets, cap.ModelPrefix, isolatedDir);
    }

    private static ProviderMaterialization MaterializeOpenCodeOverlay(
        AgentProviderCapability cap,
        ModelProviderRecord provider,
        string? apiKey,
        string effectiveBaseUrl)
    {
        var npmPackage = provider.Protocol switch
        {
            ProviderProtocol.Anthropic => "@ai-sdk/anthropic",
            ProviderProtocol.Gemini or ProviderProtocol.GeminiOfficialKey => "@ai-sdk/google",
            _ => "@ai-sdk/openai-compatible",
        };

        var optionsObj = new JsonObject
        {
            ["apiKey"] = $"{{env:{ProviderKeyEnvName}}}",
        };
        if (!string.IsNullOrWhiteSpace(effectiveBaseUrl))
        {
            optionsObj["baseURL"] = effectiveBaseUrl;
        }
        if (provider.Headers.Count > 0)
        {
            var headersObj = new JsonObject();
            foreach (var (k, v) in provider.Headers) headersObj[k] = v;
            optionsObj["headers"] = headersObj;
        }

        var modelsObj = new JsonObject();
        foreach (var model in provider.Models.Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Id)))
        {
            var mObj = new JsonObject
            {
                ["name"] = model.EffectiveLabel,
            };
            if (model.ContextWindow is > 0)
            {
                mObj["limit"] = new JsonObject { ["context"] = model.ContextWindow.Value };
            }
            modelsObj[model.Id] = mObj;
        }

        var root = new JsonObject
        {
            ["provider"] = new JsonObject
            {
                [OpenCodeProviderId] = new JsonObject
                {
                    ["name"] = provider.Name,
                    ["npm"] = npmPackage,
                    ["options"] = optionsObj,
                    ["models"] = modelsObj,
                },
            },
        };

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OpenCodeConfigContentEnv] = root.ToJsonString(),
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            env[ProviderKeyEnvName] = apiKey;
        }

        var unsets = cap.UnsetEnv.Where(k => !env.ContainsKey(k)).ToList();
        var prefix = string.IsNullOrWhiteSpace(cap.ModelPrefix) ? $"{OpenCodeProviderId}/" : cap.ModelPrefix;
        return new ProviderMaterialization(env, unsets, prefix, null);
    }

    /// <summary>
    /// Recursively merges two JSON objects (used to combine OpenCode's <c>OPENCODE_CONFIG_CONTENT</c> from
    /// managed providers and Octopus signal plugins without either overwriting the other).
    /// </summary>
    public static string MergeOpenCodeConfigJson(string? baseJson, string? overlayJson)
    {
        if (string.IsNullOrWhiteSpace(baseJson)) return overlayJson ?? "{}";
        if (string.IsNullOrWhiteSpace(overlayJson)) return baseJson;

        try
        {
            var baseNode = JsonNode.Parse(baseJson);
            var overlayNode = JsonNode.Parse(overlayJson);
            if (baseNode is JsonObject bObj && overlayNode is JsonObject oObj)
            {
                DeepMerge(bObj, oObj);
                return bObj.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }
        return overlayJson;
    }

    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, val) in source)
        {
            if (val is null)
            {
                target[key] = null;
                continue;
            }
            if (val is JsonObject srcObj && target[key] is JsonObject dstObj)
            {
                DeepMerge(dstObj, srcObj);
            }
            else if (val is JsonArray srcArr && target[key] is JsonArray dstArr)
            {
                var existing = new HashSet<string>(dstArr.Select(x => x?.ToJsonString() ?? ""), StringComparer.Ordinal);
                foreach (var item in srcArr)
                {
                    var serialized = item?.ToJsonString() ?? "";
                    if (existing.Add(serialized))
                    {
                        dstArr.Add(item?.DeepClone());
                    }
                }
            }
            else
            {
                target[key] = val.DeepClone();
            }
        }
    }

    /// <summary>Builds a human-readable, secret-free preview of both direct and remote-tunnel materialization for Settings UI.</summary>
    public static string BuildPreviewText(ModelProviderRecord provider)
    {
        var sb = new StringBuilder();
        var baseUrl = string.IsNullOrWhiteSpace(provider.BaseUrl) ? "<default>" : provider.BaseUrl.Trim().TrimEnd('/');
        var enabledModels = provider.Models.Where(m => m.Enabled).ToList();
        var flagship = enabledModels.FirstOrDefault(m => m.Tier == ModelSlotTier.Flagship)?.Id ?? enabledModels.FirstOrDefault()?.Id ?? "";
        var balanced = enabledModels.FirstOrDefault(m => m.Tier == ModelSlotTier.Balanced)?.Id ?? flagship;
        var fast = enabledModels.FirstOrDefault(m => m.Tier == ModelSlotTier.Fast)?.Id ?? balanced;

        if (provider.Protocol == ProviderProtocol.Anthropic)
        {
            var keyVar = string.IsNullOrWhiteSpace(provider.AuthEnvPreference) ? "ANTHROPIC_AUTH_TOKEN" : provider.AuthEnvPreference;
            sb.AppendLine("# Claude Code (Direct · Isolated Config Dir)");
            if (provider.IsolateConfigDir) sb.AppendLine($"CLAUDE_CONFIG_DIR=\"<DataDirectory>/provider-homes/claude/{provider.Id}\"");
            sb.AppendLine($"ANTHROPIC_BASE_URL=\"{baseUrl}\"");
            sb.AppendLine($"{keyVar}=\"<injected from ISecretStore at launch>\"");
            if (!string.IsNullOrEmpty(flagship)) sb.AppendLine($"ANTHROPIC_DEFAULT_OPUS_MODEL=\"{flagship}\"");
            if (!string.IsNullOrEmpty(balanced)) sb.AppendLine($"ANTHROPIC_DEFAULT_SONNET_MODEL=\"{balanced}\"");
            if (!string.IsNullOrEmpty(fast)) sb.AppendLine($"ANTHROPIC_DEFAULT_HAIKU_MODEL=\"{fast}\"");
        }
        else if (provider.Protocol is ProviderProtocol.OpenAIResponses or ProviderProtocol.OpenAIChat)
        {
            var wire = provider.Protocol == ProviderProtocol.OpenAIResponses ? "responses" : "chat";
            sb.AppendLine("# 1) Codex CLI (Isolated CODEX_HOME, does not modify ~/.codex)");
            sb.AppendLine($"CODEX_HOME=\"<DataDirectory>/provider-homes/codex/{provider.Id}\"");
            sb.AppendLine($"{ProviderKeyEnvName}=\"<injected from ISecretStore at launch>\"");
            sb.AppendLine($"# config.toml -> model_provider = \"octopus\", wire_api = \"{wire}\", base_url = \"{baseUrl}\"");
            sb.AppendLine();
            sb.AppendLine("# 2) OpenCode (Deep-merged into OPENCODE_CONFIG_CONTENT)");
            sb.AppendLine($"# provider.octopus.options.baseURL = \"{baseUrl}\", model = \"octopus/<modelId>\"");
        }
        else
        {
            sb.AppendLine("# Gemini CLI / Antigravity CLI");
            sb.AppendLine("GEMINI_API_KEY=\"<injected from ISecretStore at launch>\"");
            if (provider.Protocol == ProviderProtocol.Gemini)
            {
                sb.AppendLine($"GOOGLE_GEMINI_BASE_URL=\"{baseUrl}\"");
            }
        }

        if (provider.AllowRemoteTunnel)
        {
            sb.AppendLine();
            sb.AppendLine("# Remote Host Encrypted API Tunnel (Remote CLI -> 127.0.0.1 -> SecureChannel -> Local -> Upstream)");
            sb.AppendLine("# Remote process receives ONLY a short-lived grant token; real API key never leaves this computer.");
            sb.AppendLine("BASE_URL=\"http://127.0.0.1:<ephemeral>/t/<grantToken>\"");
            sb.AppendLine("TOKEN=\"oct-tunnel-<grantToken>\"");
        }

        return sb.ToString().TrimEnd();
    }

    private void WriteCodexCatalog(string catalogPath, ModelProviderRecord provider)
    {
        var modelsArr = new JsonArray();
        var priority = 1;
        foreach (var m in provider.Models.Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Id)))
        {
            var levels = new JsonArray();
            var efforts = m.SupportedEfforts is { Count: > 0 } ? m.SupportedEfforts : ["low", "medium", "high"];
            foreach (var ef in efforts)
            {
                levels.Add((JsonNode)new JsonObject { ["effort"] = ef, ["description"] = ef });
            }

            modelsArr.Add((JsonNode)new JsonObject
            {
                ["slug"] = m.Id,
                ["display_name"] = m.EffectiveLabel,
                ["visibility"] = "list",
                ["priority"] = priority++,
                ["default_reasoning_level"] = efforts.Contains("high", StringComparer.OrdinalIgnoreCase) ? "high" : efforts[0],
                ["supported_reasoning_levels"] = levels,
            });
        }

        var root = new JsonObject { ["models"] = modelsArr };
        File.WriteAllText(catalogPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private void WriteCodexConfig(string configPath, ModelProviderRecord provider, string baseUrl, string catalogPath)
    {
        var wireApi = provider.Protocol == ProviderProtocol.OpenAIResponses ? "responses" : "chat";
        var sb = new StringBuilder();
        sb.AppendLine("# Managed by Octopus (provider overlay — native ~/.codex/config.toml is untouched)");
        sb.AppendLine("cli_auth_credentials_store = \"file\"");
        sb.AppendLine("model_provider = \"octopus\"");
        sb.AppendLine($"model_catalog_json = \"{EscapeToml(catalogPath)}\"");
        var firstModel = provider.Models.FirstOrDefault(m => m.Enabled)?.Id;
        if (!string.IsNullOrWhiteSpace(firstModel))
        {
            sb.AppendLine($"model = \"{EscapeToml(firstModel)}\"");
        }
        sb.AppendLine();
        sb.AppendLine("[model_providers.octopus]");
        sb.AppendLine($"name = \"{EscapeToml(provider.Name)}\"");
        sb.AppendLine($"base_url = \"{EscapeToml(baseUrl)}\"");
        sb.AppendLine($"env_key = \"{ProviderKeyEnvName}\"");
        sb.AppendLine($"wire_api = \"{wireApi}\"");
        if (provider.Headers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[model_providers.octopus.http_headers]");
            foreach (var (k, v) in provider.Headers)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    sb.AppendLine($"\"{EscapeToml(k)}\" = \"{EscapeToml(v)}\"");
                }
            }
        }

        // Preserve non-credential sections ([mcp_servers.*], [projects.*]) from the user's native ~/.codex/config.toml.
        var nativeConfig = Path.Combine(_home, ".codex", "config.toml");
        var preserved = ExtractCodexSafeSections(nativeConfig);
        if (!string.IsNullOrWhiteSpace(preserved))
        {
            sb.AppendLine();
            sb.Append(preserved);
        }

        File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
    }

    private static string ExtractCodexSafeSections(string nativeConfigPath)
    {
        if (!File.Exists(nativeConfigPath)) return "";
        try
        {
            var sb = new StringBuilder();
            var keep = false;
            foreach (var rawLine in File.ReadLines(nativeConfigPath))
            {
                var trimmed = rawLine.Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    keep = trimmed.StartsWith("[mcp_servers", StringComparison.Ordinal)
                        || trimmed.StartsWith("[projects", StringComparison.Ordinal);
                }
                if (keep)
                {
                    sb.AppendLine(rawLine);
                }
            }
            return sb.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private void SyncClaudeNonSecretFiles(string targetDir)
    {
        var nativeDir = Path.Combine(_home, ".claude");
        if (!Directory.Exists(nativeDir)) return;

        // Copy lightweight non-secret user guidance / skills / commands if absent; never touch credentials or oauthAccount.
        try
        {
            var claudeMd = Path.Combine(nativeDir, "CLAUDE.md");
            var destClaudeMd = Path.Combine(targetDir, "CLAUDE.md");
            if (File.Exists(claudeMd) && !File.Exists(destClaudeMd))
            {
                File.Copy(claudeMd, destClaudeMd, overwrite: false);
            }

            foreach (var subDirName in new[] { "skills", "commands" })
            {
                var src = Path.Combine(nativeDir, subDirName);
                var dst = Path.Combine(targetDir, subDirName);
                if (Directory.Exists(src) && !Directory.Exists(dst) && !File.Exists(dst))
                {
                    try
                    {
                        Directory.CreateSymbolicLink(dst, src);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        // Symbolic links may require elevated permissions on Windows; safe to skip.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
            }
        }
    }

    private static string? LookupProtocolMap(Dictionary<string, string>? map, string protocolKey)
    {
        if (map is null || map.Count == 0) return null;
        foreach (var (k, v) in map)
        {
            if (string.Equals(k, protocolKey, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }
        return null;
    }

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string SanitizeKey(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }
        return sb.Length == 0 ? "default" : sb.ToString();
    }
}
