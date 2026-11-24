using System.Net;
using System.Text;
using Octopus.Platform;

namespace Octopus.Agents.Tests;

public class ModelDiscoveryTests
{
    [Fact]
    public void Lines_parser_strips_ansi_and_skips_prose()
    {
        var parsed = ModelDiscovery.Parse("\u001b[32mopencode/big-pickle\u001b[0m\n\nSome note here\nanthropic/claude\nanthropic/claude\n", "lines");
        Assert.Equal(["opencode/big-pickle", "anthropic/claude"], parsed.Select(o => o.Id));
    }

    [Fact]
    public void IdDashLabel_parser_reads_label()
    {
        var parsed = ModelDiscovery.Parse("Available models\n\nauto - Auto (current, default)\ngpt-5.3-codex-low - Codex 5.3 Low\n", "idDashLabel");
        Assert.Equal(["auto", "gpt-5.3-codex-low"], parsed.Select(o => o.Id));
        Assert.Equal("Codex 5.3 Low", parsed[1].Label);
    }

    [Fact]
    public void IdTabLabel_parser_reads_tab_separated_label()
    {
        var parsed = ModelDiscovery.Parse("gemini-3.8-flash-high\tGemini 3.8 Flash (High)\nclaude-sonnet-4-6\tClaude Sonnet 4.6 (Thinking)\nnote without tab\n", "idTabLabel");
        Assert.Equal(["gemini-3.8-flash-high", "claude-sonnet-4-6"], parsed.Select(o => o.Id));
        Assert.Equal("Claude Sonnet 4.6 (Thinking)", parsed[1].Label);
    }

    [Fact]
    public async Task Discover_runs_once_and_caches()
    {
        var runner = new FakeRunner((_, args, _) => new ProcessResult(0, "a/b\nc/d\n", "", false));
        var discovery = new ModelDiscovery(runner, new FakeLoginEnvironment());
        var def = Builtins.Get("opencode");

        var first = await discovery.DiscoverAsync(def, "opencode");
        var second = await discovery.DiscoverAsync(def, "opencode");
        await discovery.DiscoverAsync(def, "opencode", refresh: true);

        Assert.Equal(2, first.Count);
        Assert.Same(first, second);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task Failure_or_missing_discover_gives_empty()
    {
        var runner = new FakeRunner((_, _, _) => new ProcessResult(1, "", "not logged in", false));
        var discovery = new ModelDiscovery(runner, new FakeLoginEnvironment());
        Assert.Empty(await discovery.DiscoverAsync(Builtins.Get("cursor"), "cursor-agent"));
        Assert.Empty(await discovery.DiscoverAsync(Builtins.Get("claude"), "claude"));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Discover_passes_system_proxy_when_unset_in_environment()
    {
        IReadOnlyDictionary<string, string>? seenEnv = null;
        var runner = new FakeRunner((_, _, env) =>
        {
            seenEnv = env;
            return new ProcessResult(0, "gemini-4-neon-high\tGemini 4 Neon (High)\n", "", false);
        });
        var discovery = new ModelDiscovery(runner, new FakeLoginEnvironment(), new FixedProxy(new Uri("http://127.0.0.1:7890")));

        var options = await discovery.DiscoverAsync(Builtins.Get("agy"), "agy");

        Assert.Equal(["gemini-4-neon-high"], options.Select(o => o.Id));
        Assert.Equal("http://127.0.0.1:7890", seenEnv!["HTTPS_PROXY"]);
    }

    [Fact]
    public async Task Codex_discovers_third_party_gateway_and_cache_models_and_config_model()
    {
        using var tmp = new TempDir();
        var codexHome = Path.Combine(tmp.Path, "codex");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), """
            model = "custom-pinned-model"
            openai_base_url = "http://127.0.0.1:3425/backend-api/codex"
            """);

        var seenUrls = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            seenUrls.Add(req.RequestUri!.ToString());
            const string magpieJson = """
                {
                  "models": [
                    { "slug": "hidden-internal", "display_name": "Hidden", "visibility": "hide", "priority": 0 },
                    { "slug": "gpt-6.2-astra-laai0", "display_name": "GPT-6.2 Astra · laai0", "description": "via magpie", "visibility": "list", "priority": 2 },
                    { "slug": "deepseek-v4-pro", "display_name": "DeepSeek-V4-Pro · DeepSeek", "description": "DeepSeek-V4-Pro via magpie", "visibility": "list", "priority": 1 }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(magpieJson, Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var discovery = new ModelDiscovery(new FakeRunner((_, _, _) => new ProcessResult(1, "", "", false)), new FakeLoginEnvironment(), httpClient: http);

        var options = await discovery.DiscoverAsync(
            Builtins.Get("codex"),
            "codex",
            environmentOverrides: new Dictionary<string, string> { ["CODEX_HOME"] = codexHome, ["OPENAI_API_KEY"] = "sk-magpie" });

        Assert.Single(seenUrls);
        Assert.Contains("/backend-api/codex/models", seenUrls[0]);
        Assert.Equal(["custom-pinned-model", "deepseek-v4-pro", "gpt-6.2-astra-laai0"], options.Select(o => o.Id));
        Assert.Equal("DeepSeek-V4-Pro · DeepSeek", options[1].Label);
        Assert.Equal("GPT-6.2 Astra · laai0", options[2].Label);
    }

    [Fact]
    public async Task Codex_discovers_openai_compatible_provider_models_and_falls_back_to_models_cache()
    {
        using var tmp = new TempDir();
        var codexHome = Path.Combine(tmp.Path, "codex");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), """
            model_provider = "deepseek"

            [model_providers.deepseek]
            name = "DeepSeek"
            base_url = "https://api.deepseek.example/v1"
            env_key = "DEEPSEEK_API_KEY"
            """);

        string? authHeader = null;
        var handler = new StubHttpHandler(req =>
        {
            authHeader = req.Headers.Authorization?.ToString();
            const string openaiJson = """
                {
                  "data": [
                    { "id": "deepseek-chat", "owned_by": "deepseek" },
                    { "id": "deepseek-reasoner", "owned_by": "deepseek" }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(openaiJson, Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var discovery = new ModelDiscovery(new FakeRunner((_, _, _) => new ProcessResult(1, "", "", false)), new FakeLoginEnvironment(), httpClient: http);

        var options = await discovery.DiscoverAsync(
            Builtins.Get("codex"),
            "codex",
            environmentOverrides: new Dictionary<string, string>
            {
                ["CODEX_HOME"] = codexHome,
                ["DEEPSEEK_API_KEY"] = "sk-ds-test",
            });

        Assert.Equal("Bearer sk-ds-test", authHeader);
        Assert.Equal(["deepseek-chat", "deepseek-reasoner"], options.Select(o => o.Id));
        Assert.Equal("deepseek", options[0].Note);

        // When gateway returns 404, models_cache.json is used as fallback.
        File.WriteAllText(Path.Combine(codexHome, "models_cache.json"), """
            {
              "models": [
                { "slug": "cached-third-party", "display_name": "Cached Third Party", "visibility": "list", "priority": 1 }
              ]
            }
            """);
        using var failingHttp = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var fallbackDiscovery = new ModelDiscovery(new FakeRunner((_, _, _) => new ProcessResult(1, "", "", false)), new FakeLoginEnvironment(), httpClient: failingHttp);
        var cached = await fallbackDiscovery.DiscoverAsync(
            Builtins.Get("codex"),
            "codex",
            environmentOverrides: new Dictionary<string, string> { ["CODEX_HOME"] = codexHome });

        Assert.Equal(["cached-third-party"], cached.Select(o => o.Id));
        Assert.Equal("Cached Third Party", cached[0].Label);
    }

    [Fact]
    public async Task Claude_discovers_third_party_anthropic_gateway_models_from_env_or_settings()
    {
        using var tmp = new TempDir();
        var claudeDir = Path.Combine(tmp.Path, "claude");
        Directory.CreateDirectory(claudeDir);
        File.WriteAllText(Path.Combine(claudeDir, "settings.json"), """
            {
              "env": {
                "ANTHROPIC_BASE_URL": "http://127.0.0.1:3425/anthropic",
                "ANTHROPIC_AUTH_TOKEN": "tok-123",
                "ANTHROPIC_MODEL": "deepseek-v4-pro"
              }
            }
            """);

        string? seenUrl = null;
        string? seenAuth = null;
        var handler = new StubHttpHandler(req =>
        {
            seenUrl = req.RequestUri!.ToString();
            seenAuth = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"glm-5","display_name":"GLM 5"},{"id":"qwen-coder"}]}""", Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var discovery = new ModelDiscovery(new FakeRunner((_, _, _) => new ProcessResult(1, "", "", false)), new FakeLoginEnvironment(), httpClient: http);

        var options = await discovery.DiscoverAsync(
            Builtins.Get("claude"),
            "claude",
            environmentOverrides: new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = claudeDir });

        Assert.Equal("http://127.0.0.1:3425/anthropic/v1/models", seenUrl);
        Assert.Equal("Bearer tok-123", seenAuth);
        Assert.Equal(["deepseek-v4-pro", "glm-5", "qwen-coder"], options.Select(o => o.Id));
        Assert.Equal("GLM 5", options[1].Label);
    }

    private sealed class FixedProxy(Uri? uri) : ISystemProxy
    {
        public Uri? Resolve() => uri;
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
