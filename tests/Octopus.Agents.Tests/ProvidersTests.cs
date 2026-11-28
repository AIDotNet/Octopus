using System.Text.Json;
using Octopus.Agents.Providers;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.Agents.Tests;

public sealed class ProvidersTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestAppPaths _paths;
    private readonly InMemorySecretStore _secrets;
    private readonly JsonProviderStore _store;
    private readonly ModelProviderService _service;
    private readonly ProviderMaterializer _materializer;
    private readonly IAgentCatalog _catalog;

    public ProvidersTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "octopus-providers-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new TestAppPaths(_tempDir);
        _secrets = new InMemorySecretStore();
        _store = new JsonProviderStore(_paths);
        _service = new ModelProviderService(_store, _secrets);
        _materializer = new ProviderMaterializer(_paths);
        _catalog = new AgentCatalog(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Save_stores_secret_only_in_secret_store_never_in_providers_json()
    {
        const string realSecret = "sk-ant-super-secret-value-998877";
        var saved = await _service.SaveAsync(new ModelProviderRecord
        {
            Name = "Corp Gateway",
            Protocol = ProviderProtocol.Anthropic,
            BaseUrl = "https://gw.corp.internal/anthropic",
            Models =
            [
                new ManagedModelSpec { Id = "claude-sonnet-4-5", DisplayName = "Sonnet 4.5", Tier = ModelSlotTier.Balanced },
            ],
        }, realSecret);

        Assert.True(await _service.HasSecretAsync(saved.Id));
        Assert.Equal(realSecret, await _service.ReadSecretAsync(saved.Id));

        var jsonOnDisk = await File.ReadAllTextAsync(Path.Combine(_tempDir, "providers.json"));
        Assert.Contains("Corp Gateway", jsonOnDisk);
        Assert.DoesNotContain(realSecret, jsonOnDisk);

        await _service.RemoveAsync(saved.Id);
        Assert.False(await _service.HasSecretAsync(saved.Id));
        Assert.Null(_service.Find(saved.Id));
    }

    [Fact]
    public async Task Compatible_providers_filter_by_agent_protocol_and_remote_tunnel_flag()
    {
        var anthropic = await _service.SaveAsync(new ModelProviderRecord
        {
            Name = "Anthropic GW",
            Protocol = ProviderProtocol.Anthropic,
            BaseUrl = "https://gw.example/anthropic",
            AllowRemoteTunnel = true,
        }, "k1");

        var openai = await _service.SaveAsync(new ModelProviderRecord
        {
            Name = "OpenAI GW",
            Protocol = ProviderProtocol.OpenAIResponses,
            BaseUrl = "https://gw.example/v1",
            AllowRemoteTunnel = false,
        }, "k2");

        var geminiOfficial = await _service.SaveAsync(new ModelProviderRecord
        {
            Name = "Gemini Official",
            Protocol = ProviderProtocol.GeminiOfficialKey,
            BaseUrl = "",
            AllowRemoteTunnel = true,
        }, "k3");

        var claude = _catalog.Find("claude")!;
        var codex = _catalog.Find("codex")!;
        var agy = _catalog.Find("agy")!;
        var opencode = _catalog.Find("opencode")!;

        Assert.Single(_service.ListCompatibleProviders(claude), p => p.Id == anthropic.Id);
        Assert.Single(_service.ListCompatibleProviders(codex), p => p.Id == openai.Id);
        Assert.Empty(_service.ListCompatibleProviders(codex, requireRemoteTunnel: true));
        Assert.Single(_service.ListCompatibleProviders(agy), p => p.Id == geminiOfficial.Id);
        Assert.Equal(3, _service.ListCompatibleProviders(opencode).Count);
        Assert.Equal(2, _service.ListCompatibleProviders(opencode, requireRemoteTunnel: true).Count);
    }

    [Fact]
    public void Materialize_claude_sets_env_tier_slots_isolated_config_dir_and_unsets_conflicting_key()
    {
        var claude = _catalog.Find("claude")!;
        var provider = new ModelProviderRecord
        {
            Id = "corp-gw",
            Name = "Corp GW",
            Protocol = ProviderProtocol.Anthropic,
            BaseUrl = "https://gw.corp.example/anthropic",
            AuthEnvPreference = "ANTHROPIC_AUTH_TOKEN",
            IsolateConfigDir = true,
            Models =
            [
                new ManagedModelSpec { Id = "corp-opus", Tier = ModelSlotTier.Flagship, Enabled = true },
                new ManagedModelSpec { Id = "corp-sonnet", Tier = ModelSlotTier.Balanced, Enabled = true },
                new ManagedModelSpec { Id = "corp-haiku", Tier = ModelSlotTier.Fast, Enabled = true },
            ],
        };

        var result = _materializer.Materialize(claude, provider, "secret-token-123");

        Assert.Equal("https://gw.corp.example/anthropic", result.Environment["ANTHROPIC_BASE_URL"]);
        Assert.Equal("secret-token-123", result.Environment["ANTHROPIC_AUTH_TOKEN"]);
        Assert.False(result.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Contains("ANTHROPIC_API_KEY", result.UnsetEnvironment);
        Assert.Equal("corp-opus", result.Environment["ANTHROPIC_DEFAULT_OPUS_MODEL"]);
        Assert.Equal("corp-sonnet", result.Environment["ANTHROPIC_DEFAULT_SONNET_MODEL"]);
        Assert.Equal("corp-haiku", result.Environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.True(result.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var isolatedDir));
        Assert.True(Directory.Exists(isolatedDir));
        Assert.StartsWith(_tempDir, isolatedDir);
    }

    [Fact]
    public void Materialize_codex_generates_isolated_codex_home_config_toml_and_catalog()
    {
        var codex = _catalog.Find("codex")!;
        var provider = new ModelProviderRecord
        {
            Id = "codex-gw",
            Name = "Codex Proxy",
            Protocol = ProviderProtocol.OpenAIResponses,
            BaseUrl = "https://proxy.example/v1",
            Models =
            [
                new ManagedModelSpec
                {
                    Id = "gpt-5.4",
                    DisplayName = "GPT 5.4",
                    Enabled = true,
                    SupportedEfforts = ["low", "high"],
                },
            ],
        };

        var result = _materializer.Materialize(codex, provider, "sk-codex-key", selectedModelId: "gpt-5.4");

        Assert.True(result.Environment.TryGetValue("CODEX_HOME", out var codexHome));
        Assert.StartsWith(_tempDir, codexHome);
        Assert.Equal("sk-codex-key", result.Environment[ProviderMaterializer.ProviderKeyEnvName]);

        var toml = File.ReadAllText(Path.Combine(codexHome, "config.toml"));
        Assert.Contains("model_provider = \"octopus\"", toml);
        Assert.Contains("base_url = \"https://proxy.example/v1\"", toml);
        Assert.Contains($"env_key = \"{ProviderMaterializer.ProviderKeyEnvName}\"", toml);
        Assert.Contains("wire_api = \"responses\"", toml);

        var catalogJson = File.ReadAllText(Path.Combine(codexHome, "catalog.json"));
        using var doc = JsonDocument.Parse(catalogJson);
        Assert.Equal("gpt-5.4", doc.RootElement.GetProperty("models")[0].GetProperty("slug").GetString());
    }

    [Fact]
    public void Materialize_opencode_and_merge_preserves_signal_plugins()
    {
        var opencode = _catalog.Find("opencode")!;
        var provider = new ModelProviderRecord
        {
            Id = "oc-gw",
            Name = "OpenCode GW",
            Protocol = ProviderProtocol.OpenAIChat,
            BaseUrl = "https://oc.example/v1",
            Models =
            [
                new ManagedModelSpec { Id = "deepseek-r1", DisplayName = "DeepSeek R1", Enabled = true },
            ],
        };

        var result = _materializer.Materialize(opencode, provider, "sk-oc-secret");
        Assert.Equal("octopus/", result.ModelPrefix);
        Assert.True(result.Environment.TryGetValue("OPENCODE_CONFIG_CONTENT", out var providerJson));

        var signalJson = """{"plugin":["/tmp/octopus-signal-plugin.js"]}""";
        var merged = ProviderMaterializer.MergeOpenCodeConfigJson(providerJson!, signalJson);

        using var doc = JsonDocument.Parse(merged);
        Assert.Equal("/tmp/octopus-signal-plugin.js", doc.RootElement.GetProperty("plugin")[0].GetString());
        Assert.Equal("https://oc.example/v1", doc.RootElement.GetProperty("provider").GetProperty("octopus").GetProperty("options").GetProperty("baseURL").GetString());
    }

    [Fact]
    public void Materialize_tunnel_points_to_loopback_with_placeholder_token_and_zero_real_keys()
    {
        var claude = _catalog.Find("claude")!;
        var spec = new RemoteTunnelProviderSpec
        {
            GrantToken = "grant-abcdef1234567890",
            DisplayName = "My Mac Corp GW",
            Protocol = ProviderProtocol.Anthropic,
            Models = [new ManagedModelSpec { Id = "claude-sonnet-4-5", Tier = ModelSlotTier.Balanced, Enabled = true }],
        };

        var result = _materializer.MaterializeTunnel(claude, spec, "http://127.0.0.1:54321/t/grant-abcdef1234567890");
        Assert.Equal("http://127.0.0.1:54321/t/grant-abcdef1234567890", result.Environment["ANTHROPIC_BASE_URL"]);
        Assert.StartsWith("oct-tunnel-", result.Environment["ANTHROPIC_AUTH_TOKEN"]);
    }

    [Fact]
    public async Task Backend_resolver_enforces_4_tier_precedence_and_falls_back_when_disabled()
    {
        var pProfile = await _service.SaveAsync(new ModelProviderRecord
        {
            Name = "Profile GW",
            Protocol = ProviderProtocol.Anthropic,
            BaseUrl = "https://profile.example",
        }, "k-profile");
        var pProject = await _service.SaveAsync(new ModelProviderRecord
        {
            Name = "Project GW",
            Protocol = ProviderProtocol.Anthropic,
            BaseUrl = "https://project.example",
        }, "k-project");
        var pLaunch = await _service.SaveAsync(new ModelProviderRecord
        {
            Name = "Launch GW",
            Protocol = ProviderProtocol.Anthropic,
            BaseUrl = "https://launch.example",
        }, "k-launch");

        var resolver = new AgentBackendResolver(_service, _materializer);
        var claude = _catalog.Find("claude")!;
        var profile = new AgentProfile { DefaultBackend = AgentBackendRefs.Provider(pProfile.Id) };
        var project = new Project { AgentBackends = { ["claude"] = AgentBackendRefs.Provider(pProject.Id) } };
        var launch = new AgentLaunchOptions { BackendRef = AgentBackendRefs.Provider(pLaunch.Id) };

        // Tier 1: Launch wins over Project & Profile
        var r1 = await resolver.ResolveAsync(claude, profile, project, launch);
        Assert.Equal(pLaunch.Id, r1.Provider!.Id);

        // Tier 2: Project wins over Profile
        var r2 = await resolver.ResolveAsync(claude, profile, project, launch: null);
        Assert.Equal(pProject.Id, r2.Provider!.Id);

        // Tier 3: Profile wins when Project has no override
        var r3 = await resolver.ResolveAsync(claude, profile, project: null, launch: null);
        Assert.Equal(pProfile.Id, r3.Provider!.Id);

        // Fallback when provider is disabled
        _service.SetEnabled(pProfile.Id, false);
        var r4 = await resolver.ResolveAsync(claude, profile, project: null, launch: null);
        Assert.False(r4.IsProvider);
    }

    private sealed class TestAppPaths(string dir) : IAppPaths
    {
        public string DataDirectory => dir;
        public string CacheDirectory => dir;
        public string LogDirectory => dir;
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<(string Service, string Key), string> _map = [];
        public bool IsAvailable => true;

        public Task<string?> GetAsync(string service, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_map.GetValueOrDefault((service, key)));

        public Task SetAsync(string service, string key, string secret, CancellationToken cancellationToken = default)
        {
            _map[(service, key)] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string service, string key, CancellationToken cancellationToken = default)
        {
            _map.Remove((service, key));
            return Task.CompletedTask;
        }
    }
}
