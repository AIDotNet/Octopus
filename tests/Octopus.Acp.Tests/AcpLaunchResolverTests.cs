using Octopus.Acp.Process;
using Octopus.Core.Agents;
using Octopus.Core.Settings;
using Octopus.Platform;

namespace Octopus.Acp.Tests;

public class AcpLaunchResolverTests
{
    private sealed class FakeLocator(Dictionary<string, string> binaries) : IExecutableLocator
    {
        public IReadOnlyList<string> FindAll(string name, string? pathOverride = null) =>
            binaries.TryGetValue(name, out var full) ? [full] : [];
    }

    private sealed class FakeLoginEnv : ILoginEnvironment
    {
        public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
            {
                ["PATH"] = "/usr/local/bin:/usr/bin",
            });
    }

    [Fact]
    public async Task Resolves_native_acp_with_env_and_grok_full_access_insertion()
    {
        var locator = new FakeLocator(new() { ["grok"] = "/usr/local/bin/grok" });
        var resolver = new AcpLaunchResolver(locator, new FakeLoginEnv());
        var def = new AgentDefinition
        {
            Id = "grok",
            Name = "Grok",
            Detect = new() { Executables = ["grok"] },
            Acp = new()
            {
                Mode = "native",
                Args = ["agent", "stdio"],
                FullAccessArgs = ["--always-approve"],
                Env = new() { ["GROK_ACP"] = "1" },
                Dialect = "grok",
            },
        };
        var install = new AgentInstallation { AgentId = "grok", Path = "/usr/local/bin/grok", Version = "1.0.0" };
        var plan = await resolver.ResolveAsync(def, install, new AgentSettings(), "/tmp/repo", PermissionModes.Full);

        Assert.Equal("/usr/local/bin/grok", plan.Executable);
        Assert.Equal(["agent", "--always-approve", "stdio"], plan.Arguments);
        Assert.Equal("1", plan.Environment["GROK_ACP"]);
        Assert.Equal("grok", plan.DialectId);
    }

    [Fact]
    public async Task Resolves_adapter_local_binary_then_npx_fallback_and_respects_native_only()
    {
        var def = new AgentDefinition
        {
            Id = "claude",
            Name = "Claude Code",
            Acp = new()
            {
                Mode = "adapter",
                Dialect = "claude-acp",
                Adapter = new()
                {
                    RegistryId = "claude-acp",
                    Executables = ["claude-agent-acp"],
                    NpxPackage = "@agentclientprotocol/claude-agent-acp",
                },
            },
        };

        // 1. Local adapter present -> prefers local binary even under NativeOnly.
        var localResolver = new AcpLaunchResolver(
            new FakeLocator(new() { ["claude-agent-acp"] = "/usr/local/bin/claude-agent-acp", ["npx"] = "/usr/local/bin/npx" }),
            new FakeLoginEnv());
        var nativeOnly = new AgentSettings { AcpAdapterPolicy = AcpAdapterPolicy.NativeOnly };
        Assert.True(localResolver.CanLaunchAcp(def, null, nativeOnly));
        var localPlan = await localResolver.ResolveAsync(def, null, nativeOnly, "/tmp/repo", PermissionModes.Default);
        Assert.Equal("/usr/local/bin/claude-agent-acp", localPlan.Executable);

        // 2. Only npx present -> refused under NativeOnly, allowed under UseLocalRunners.
        var npxResolver = new AcpLaunchResolver(
            new FakeLocator(new() { ["npx"] = "/usr/local/bin/npx" }),
            new FakeLoginEnv());
        Assert.False(npxResolver.CanLaunchAcp(def, null, nativeOnly));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            npxResolver.ResolveAsync(def, null, nativeOnly, "/tmp/repo", PermissionModes.Default));

        var runners = new AgentSettings { AcpAdapterPolicy = AcpAdapterPolicy.UseLocalRunners };
        Assert.True(npxResolver.CanLaunchAcp(def, null, runners));
        var npxPlan = await npxResolver.ResolveAsync(def, null, runners, "/tmp/repo", PermissionModes.Default);
        Assert.Equal("/usr/local/bin/npx", npxPlan.Executable);
        Assert.Equal(["-y", "@agentclientprotocol/claude-agent-acp"], npxPlan.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(180), npxPlan.InitializeTimeout);
    }

    private sealed class TempAppPaths : IAppPaths, IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("octopus-acp-test-").FullName;
        public string DataDirectory => Path.Combine(_root, "data");
        public string CacheDirectory => Path.Combine(_root, "cache");
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    }

    [Fact]
    public async Task Prefers_cached_managed_adapter_and_propagates_native_executable_env_var()
    {
        using var paths = new TempAppPaths();
        var registry = new AcpRegistryClient(paths);
        var installer = new AcpAdapterInstaller(paths, registry);

        var binDir = Path.Combine(installer.AdapterRoot("claude"), "npm", "node_modules", ".bin");
        Directory.CreateDirectory(binDir);
        var fakeAdapter = Path.Combine(binDir, OperatingSystem.IsWindows() ? "claude-agent-acp.cmd" : "claude-agent-acp");
        await File.WriteAllTextAsync(fakeAdapter, "#!/bin/sh\n");

        var claudeDef = new Octopus.Agents.AgentCatalog(paths).Find("claude")!;
        Assert.True(claudeDef.Acp!.Adapter!.NpxOmitOptionalWhenInstalled);
        Assert.Equal("CLAUDE_CODE_EXECUTABLE", claudeDef.Acp.Adapter.NativeExecutableEnvVar);

        var resolver = new AcpLaunchResolver(
            new FakeLocator(new()),
            new FakeLoginEnv(),
            adapterInstaller: installer);
        var install = new AgentInstallation { AgentId = "claude", Path = "/opt/claude/bin/claude", Version = "2.1.0" };
        var plan = await resolver.ResolveAsync(claudeDef, install, new AgentSettings(), "/tmp/repo", PermissionModes.Default);

        Assert.Equal(fakeAdapter, plan.Executable);
        Assert.Equal("/opt/claude/bin/claude", plan.Environment["CLAUDE_CODE_EXECUTABLE"]);
        Assert.Equal(TimeSpan.FromSeconds(60), plan.InitializeTimeout);
    }

    [Fact]
    public async Task Adapter_uses_profile_command_override_for_native_executable_env_var()
    {
        using var paths = new TempAppPaths();
        var registry = new AcpRegistryClient(paths);
        var installer = new AcpAdapterInstaller(paths, registry);

        var binDir = Path.Combine(installer.AdapterRoot("claude"), "npm", "node_modules", ".bin");
        Directory.CreateDirectory(binDir);
        var fakeAdapter = Path.Combine(binDir, OperatingSystem.IsWindows() ? "claude-agent-acp.cmd" : "claude-agent-acp");
        await File.WriteAllTextAsync(fakeAdapter, "#!/bin/sh\n");

        var claudeDef = new Octopus.Agents.AgentCatalog(paths).Find("claude")!;
        var resolver = new AcpLaunchResolver(
            new FakeLocator(new() { ["rec"] = "/Users/token/.local/bin/rec" }),
            new FakeLoginEnvWithVar("CLAUDE_CODE_EXECUTABLE", "/stale/shell/claude"),
            adapterInstaller: installer);

        var settings = new AgentSettings();
        settings.Profiles["claude"] = new AgentProfile { Command = "rec" };
        var install = new AgentInstallation { AgentId = "claude", Path = "/opt/claude/bin/claude", Version = "2.1.0" };

        var plan = await resolver.ResolveAsync(claudeDef, install, settings, "/tmp/repo", PermissionModes.Default);

        Assert.Equal(fakeAdapter, plan.Executable);
        Assert.Equal("/Users/token/.local/bin/rec", plan.Environment["CLAUDE_CODE_EXECUTABLE"]);
        Assert.EndsWith("→ rec", plan.SourceLabel);
    }

    [Fact]
    public async Task Adapter_respects_explicit_profile_env_over_profile_command()
    {
        using var paths = new TempAppPaths();
        var registry = new AcpRegistryClient(paths);
        var installer = new AcpAdapterInstaller(paths, registry);

        var binDir = Path.Combine(installer.AdapterRoot("claude"), "npm", "node_modules", ".bin");
        Directory.CreateDirectory(binDir);
        var fakeAdapter = Path.Combine(binDir, OperatingSystem.IsWindows() ? "claude-agent-acp.cmd" : "claude-agent-acp");
        await File.WriteAllTextAsync(fakeAdapter, "#!/bin/sh\n");

        var claudeDef = new Octopus.Agents.AgentCatalog(paths).Find("claude")!;
        var resolver = new AcpLaunchResolver(
            new FakeLocator(new() { ["rec"] = "/Users/token/.local/bin/rec" }),
            new FakeLoginEnv(),
            adapterInstaller: installer);

        var settings = new AgentSettings();
        settings.Profiles["claude"] = new AgentProfile
        {
            Command = "rec",
            Env = new Dictionary<string, string> { ["CLAUDE_CODE_EXECUTABLE"] = "/explicit/bin/claude" },
        };
        var install = new AgentInstallation { AgentId = "claude", Path = "/opt/claude/bin/claude", Version = "2.1.0" };

        var plan = await resolver.ResolveAsync(claudeDef, install, settings, "/tmp/repo", PermissionModes.Default);

        Assert.Equal("/explicit/bin/claude", plan.Environment["CLAUDE_CODE_EXECUTABLE"]);
        Assert.DoesNotContain("→ rec", plan.SourceLabel);
    }

    [Fact]
    public async Task Adapter_wraps_profile_command_with_prefix_arguments_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;

        using var paths = new TempAppPaths();
        var registry = new AcpRegistryClient(paths);
        var installer = new AcpAdapterInstaller(paths, registry);

        var binDir = Path.Combine(installer.AdapterRoot("claude"), "npm", "node_modules", ".bin");
        Directory.CreateDirectory(binDir);
        var fakeAdapter = Path.Combine(binDir, "claude-agent-acp");
        await File.WriteAllTextAsync(fakeAdapter, "#!/bin/sh\n");

        var claudeDef = new Octopus.Agents.AgentCatalog(paths).Find("claude")!;
        var resolver = new AcpLaunchResolver(
            new FakeLocator(new() { ["rec"] = "/Users/token/.local/bin/rec" }),
            new FakeLoginEnv(),
            adapterInstaller: installer);

        var settings = new AgentSettings();
        settings.Profiles["claude"] = new AgentProfile { Command = "rec cli --profile work" };
        var install = new AgentInstallation { AgentId = "claude", Path = "/opt/claude/bin/claude", Version = "2.1.0" };

        var plan = await resolver.ResolveAsync(claudeDef, install, settings, "/tmp/repo", PermissionModes.Default);

        var wrapper = plan.Environment["CLAUDE_CODE_EXECUTABLE"];
        Assert.NotEqual("/Users/token/.local/bin/rec", wrapper);
        Assert.True(File.Exists(wrapper));
        var content = await File.ReadAllTextAsync(wrapper);
        Assert.Contains("exec /Users/token/.local/bin/rec cli --profile work \"$@\"", content);
    }

    [Fact]
    public async Task Native_acp_resolves_profile_command_from_path_with_prefix_args()
    {
        var locator = new FakeLocator(new() { ["my-grok"] = "/Users/token/.local/bin/my-grok" });
        var resolver = new AcpLaunchResolver(locator, new FakeLoginEnv());
        var def = new AgentDefinition
        {
            Id = "grok",
            Name = "Grok",
            Detect = new() { Executables = ["grok"] },
            Acp = new()
            {
                Mode = "native",
                Args = ["agent", "stdio"],
                Dialect = "grok",
            },
        };
        var settings = new AgentSettings();
        settings.Profiles["grok"] = new AgentProfile { Command = "my-grok --profile work" };

        var plan = await resolver.ResolveAsync(def, null, settings, "/tmp/repo", PermissionModes.Default);

        Assert.Equal("/Users/token/.local/bin/my-grok", plan.Executable);
        Assert.Equal(["--profile", "work", "agent", "stdio"], plan.Arguments);
    }

    [Fact]
    public async Task Codex_adapter_preserves_explicit_third_party_env_passes_codex_path_and_injects_initial_model()
    {
        using var paths = new TempAppPaths();
        var registry = new AcpRegistryClient(paths);
        var installer = new AcpAdapterInstaller(paths, registry);

        var binDir = Path.Combine(installer.AdapterRoot("codex"), "npm", "node_modules", ".bin");
        Directory.CreateDirectory(binDir);
        var fakeAdapter = Path.Combine(binDir, OperatingSystem.IsWindows() ? "codex-acp.cmd" : "codex-acp");
        await File.WriteAllTextAsync(fakeAdapter, "#!/bin/sh\n");

        var codexDef = new Octopus.Agents.AgentCatalog(paths).Find("codex")!;
        Assert.Equal("CODEX_PATH", codexDef.Acp!.Adapter!.NativeExecutableEnvVar);
        Assert.False(codexDef.Acp.Adapter.ResolveWindowsBatchShim);
        Assert.Contains(codexDef.Capabilities.Env!.Known, v => v.Name == "OPENAI_BASE_URL");

        var resolver = new AcpLaunchResolver(
            new FakeLocator(new()),
            new FakeLoginEnvWithVar("OPENAI_API_KEY", "sk-shell-stale"),
            adapterInstaller: installer);

        var settings = new AgentSettings();
        settings.Profiles["codex"] = new AgentProfile
        {
            Env = new Dictionary<string, string>
            {
                ["OPENAI_API_KEY"] = "sk-third-party",
                ["OPENAI_BASE_URL"] = "http://127.0.0.1:3425/backend-api/codex",
            },
            Features = new Dictionary<string, string>
            {
                ["codexHome"] = "/tmp/custom-codex",
            },
        };
        var install = new AgentInstallation { AgentId = "codex", Path = "/usr/local/bin/codex", Version = "0.171.0" };

        // Even when account isolation passes unsetEnv for OPENAI_API_KEY and CODEX_HOME, explicit profile/feature env MUST win!
        var plan = await resolver.ResolveAsync(
            codexDef,
            install,
            settings,
            "/tmp/repo",
            PermissionModes.Default,
            accountEnv: null,
            unsetEnv: ["OPENAI_API_KEY", "CODEX_HOME"],
            initialModelId: "deepseek-v4-pro",
            initialEffortId: "high");

        Assert.Equal(fakeAdapter, plan.Executable);
        Assert.Equal("/usr/local/bin/codex", plan.Environment["CODEX_PATH"]);
        Assert.Equal("sk-third-party", plan.Environment["OPENAI_API_KEY"]);
        Assert.Equal("http://127.0.0.1:3425/backend-api/codex", plan.Environment["OPENAI_BASE_URL"]);
        Assert.Equal("/tmp/custom-codex", plan.Environment["CODEX_HOME"]);
        Assert.DoesNotContain("OPENAI_API_KEY", plan.UnsetEnvironment);
        Assert.DoesNotContain("CODEX_HOME", plan.UnsetEnvironment);
        Assert.Contains("\"model\":\"deepseek-v4-pro\"", plan.Environment["CODEX_CONFIG"]);
        Assert.Contains("\"model_reasoning_effort\":\"high\"", plan.Environment["CODEX_CONFIG"]);
    }

    private sealed class FakeLoginEnvWithVar(string key, string value) : ILoginEnvironment
    {
        public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
            {
                ["PATH"] = "/usr/local/bin:/usr/bin",
                [key] = value,
            });
    }
}
