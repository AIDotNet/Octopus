using System.Text;
using System.Text.Json;
using Octopus.Agents.Accounts;
using Octopus.Agents.Signals;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.Agents.Tests;

public sealed class AgentAccountServiceTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly string _home;
    private readonly TestPaths _paths;
    private readonly InMemoryAccountStore _store = new();
    private readonly ISecretStore _secrets = new MemorySecretStore();
    private readonly SignalHub _hub;
    private readonly AgentAccountService _service;

    public AgentAccountServiceTests()
    {
        _home = Path.Combine(_root.Path, "home");
        Directory.CreateDirectory(_home);
        _paths = new TestPaths(_root.Path);
        _hub = new SignalHub(_paths, Builtins.All);
        _service = new AgentAccountService(_store, _paths, _secrets, new TestShellBuilder(), _hub, _home);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void Builtin_catalog_declares_accounts_and_round_trips_through_source_gen()
    {
        var claude = Builtins.Get("claude");
        var codex = Builtins.Get("codex");
        var copilot = Builtins.Get("copilot");
        var agy = Builtins.Get("agy");

        Assert.Equal(AccountModes.EnvDir, claude.Accounts!.Mode);
        Assert.Equal("CLAUDE_CONFIG_DIR", claude.Accounts.Env);
        Assert.Equal(["auth", "login", "--claudeai"], claude.Accounts.LoginArgs);
        Assert.Contains("ANTHROPIC_API_KEY", claude.Accounts.UnsetEnv!);

        Assert.Equal(AccountModes.EnvDir, codex.Accounts!.Mode);
        Assert.Equal("CODEX_HOME", codex.Accounts.Env);
        Assert.Equal(["login"], codex.Accounts.LoginArgs);
        Assert.Contains("cli_auth_credentials_store", codex.Accounts.InitFiles!["config.toml"]);

        Assert.Equal(AccountModes.EnvDir, copilot.Accounts!.Mode);
        Assert.Equal("COPILOT_HOME", copilot.Accounts.Env);
        Assert.Contains("GH_TOKEN", copilot.Accounts.UnsetEnv!);

        Assert.Equal(AccountModes.GlobalKeyring, agy.Accounts!.Mode);
        Assert.Equal("gemini", agy.Accounts.Keyring!.Service);
        Assert.Equal("antigravity", agy.Accounts.Keyring.Account);
        Assert.Equal("gemini:antigravity", agy.Accounts.Keyring.WindowsTarget);

        var qoder = Builtins.Get("qoder");
        var qoderCn = Builtins.Get("qoder-cn");
        var kimi = Builtins.Get("kimi");
        var cline = Builtins.Get("cline");

        Assert.Equal(AccountModes.EnvDir, qoder.Accounts!.Mode);
        Assert.Equal("QODER_CONFIG_DIR", qoder.Accounts.Env);
        Assert.Contains("QODER_PERSONAL_ACCESS_TOKEN", qoder.Accounts.UnsetEnv!);

        Assert.Equal(AccountModes.EnvDir, qoderCn.Accounts!.Mode);
        Assert.Equal("QODERCN_CONFIG_DIR", qoderCn.Accounts.Env);
        Assert.Contains("QODER_PERSONAL_ACCESS_TOKEN", qoderCn.Accounts.UnsetEnv!);

        Assert.Equal(AccountModes.EnvDir, kimi.Accounts!.Mode);
        Assert.Equal("KIMI_CODE_HOME", kimi.Accounts.Env);

        Assert.Equal(AccountModes.EnvDir, cline.Accounts!.Mode);
        Assert.Equal("CLINE_DIR", cline.Accounts.Env);

        foreach (var def in new[] { claude, codex, copilot, agy, qoder, qoderCn, kimi, cline })
        {
            var json = JsonSerializer.Serialize(def, AgentJsonContext.Default.AgentDefinition);
            var roundTripped = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentDefinition)!;
            Assert.Equal(def.Accounts!.Mode, roundTripped.Accounts!.Mode);
        }
    }

    [Fact]
    public async Task Managed_and_imported_directories_isolate_files_and_protect_external_folders_on_delete()
    {
        var codex = Builtins.Get("codex");
        var managed = _service.CreateManaged(codex, "Work Codex");

        Assert.Equal(AccountOrigin.Managed, managed.Origin);
        Assert.True(Directory.Exists(managed.Directory));
        var configToml = Path.Combine(managed.Directory!, "config.toml");
        Assert.True(File.Exists(configToml));
        Assert.Contains("cli_auth_credentials_store = \"file\"", File.ReadAllText(configToml));

        // If Codex rewrites config.toml without cli_auth_credentials_store, EnsurePrepared restores it at the top.
        File.WriteAllText(configToml, "model = \"gpt-5.4\"\n");
        _service.EnsurePrepared(codex, managed);
        var merged = File.ReadAllText(configToml);
        Assert.Contains("cli_auth_credentials_store = \"file\"", merged);
        Assert.Contains("model = \"gpt-5.4\"", merged);

        // External folder import + safe removal (never deletes external files).
        var externalDir = Path.Combine(_home, ".codex-personal");
        Directory.CreateDirectory(externalDir);
        File.WriteAllText(Path.Combine(externalDir, "keep.txt"), "precious");

        var imported = _service.ImportDirectory(codex, externalDir, "Personal");
        Assert.Equal(AccountOrigin.External, imported.Origin);
        Assert.Equal(3, _service.GetAccounts(codex).Count); // default + managed + external

        await _service.RemoveAsync(codex, imported.Id, deleteManagedFiles: true, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(externalDir, "keep.txt")));

        await _service.RemoveAsync(codex, managed.Id, deleteManagedFiles: true, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(managed.Directory));

        // Implicit default account can never be removed.
        await _service.RemoveAsync(codex, AgentAccountRecord.DefaultId, deleteManagedFiles: true, TestContext.Current.CancellationToken);
        Assert.Single(_service.GetAccounts(codex));
    }

    [Fact]
    public void Four_tier_precedence_resolves_default_profile_project_and_launch_accounts()
    {
        var claude = Builtins.Get("claude");
        var accProfile = _service.CreateManaged(claude, "Profile Acc");
        var accProject = _service.CreateManaged(claude, "Project Acc");
        var accLaunch = _service.CreateManaged(claude, "Launch Acc");

        // Tier 1: Implicit default -> leaves CLAUDE_CONFIG_DIR unset in Environment, and puts CLAUDE_CONFIG_DIR in UnsetEnvironment!
        var r1 = _service.Resolve(claude, new AgentProfile());
        Assert.Equal(AgentAccountRecord.DefaultId, r1.Account!.Id);
        Assert.Null(r1.Environment);
        Assert.Contains("CLAUDE_CONFIG_DIR", r1.UnsetEnvironment!);
        Assert.Contains("ANTHROPIC_API_KEY", r1.UnsetEnvironment!);

        // Tier 2: Global Profile DefaultAccountId
        var profile = new AgentProfile { DefaultAccountId = accProfile.Id };
        var r2 = _service.Resolve(claude, profile);
        Assert.Equal(accProfile.Id, r2.Account!.Id);
        Assert.Equal(accProfile.Directory, r2.Environment!["CLAUDE_CONFIG_DIR"]);

        // Tier 3: Project.AgentAccounts overrides Profile
        var project = new Project
        {
            Path = _root.Path,
            AgentAccounts = { ["claude"] = accProject.Id },
        };
        var r3 = _service.Resolve(claude, profile, project);
        Assert.Equal(accProject.Id, r3.Account!.Id);
        Assert.Equal(accProject.Directory, r3.Environment!["CLAUDE_CONFIG_DIR"]);

        // Tier 4: Per-tab AgentLaunchOptions.AccountId overrides Project & Profile (including choosing "default" explicitly!)
        var r4 = _service.Resolve(claude, profile, project, new AgentLaunchOptions { AccountId = accLaunch.Id });
        Assert.Equal(accLaunch.Id, r4.Account!.Id);
        Assert.Equal(accLaunch.Directory, r4.Environment!["CLAUDE_CONFIG_DIR"]);

        var r4Default = _service.Resolve(claude, profile, project, new AgentLaunchOptions { AccountId = AgentAccountRecord.DefaultId });
        Assert.Equal(AgentAccountRecord.DefaultId, r4Default.Account!.Id);
        Assert.Null(r4Default.Environment);
        Assert.Contains("CLAUDE_CONFIG_DIR", r4Default.UnsetEnvironment!);

        // Stale / deleted account id falls back gracefully down the chain.
        var stale = _service.Resolve(claude, profile, project, new AgentLaunchOptions { AccountId = "deleted-id" });
        Assert.Equal(accProject.Id, stale.Account!.Id);
    }

    [Fact]
    public void Compiler_unsets_conflicting_api_keys_unless_explicitly_set_in_profile_or_launch()
    {
        var compiler = new AgentCommandCompiler(new TestShellBuilder());
        var claude = Builtins.Get("claude");
        var managed = _service.CreateManaged(claude, "Sub");

        var resolved = _service.Resolve(claude, new AgentProfile { DefaultAccountId = managed.Id });
        var cmd = compiler.Compile(new AgentCompileRequest(
            claude,
            new AgentProfile { DefaultAccountId = managed.Id },
            null,
            PermissionModes.Default,
            null,
            AccountEnvironment: resolved.Environment,
            UnsetEnvironment: resolved.UnsetEnvironment));

        Assert.Equal(managed.Directory, cmd.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Contains("ANTHROPIC_API_KEY", cmd.UnsetEnvironment!);
        Assert.Contains("ANTHROPIC_AUTH_TOKEN", cmd.UnsetEnvironment!);

        // If the user explicitly configured ANTHROPIC_API_KEY in Profile.Env, Compiler keeps it and does NOT unset it.
        var apiProfile = new AgentProfile
        {
            DefaultAccountId = managed.Id,
            Env = { ["ANTHROPIC_API_KEY"] = "sk-ant-explicit" },
        };
        var cmdWithExplicitKey = compiler.Compile(new AgentCompileRequest(
            claude,
            apiProfile,
            null,
            PermissionModes.Default,
            null,
            AccountEnvironment: resolved.Environment,
            UnsetEnvironment: resolved.UnsetEnvironment));

        Assert.Equal("sk-ant-explicit", cmdWithExplicitKey.Environment["ANTHROPIC_API_KEY"]);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", cmdWithExplicitKey.UnsetEnvironment ?? []);

        // BuildLoginCommand ALWAYS unsets ANTHROPIC_API_KEY even if present in profile.Env so OAuth login can succeed.
        var loginCmd = _service.BuildLoginCommand(claude, apiProfile, null, managed);
        Assert.Equal(["auth", "login", "--claudeai"], loginCmd.Arguments);
        Assert.Equal(managed.Directory, loginCmd.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.False(loginCmd.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Contains("ANTHROPIC_API_KEY", loginCmd.UnsetEnvironment!);
    }

    [Fact]
    public async Task Local_identity_readers_extract_email_login_and_tier_without_persisting_tokens()
    {
        // 1. Claude default (~/.claude.json fallback) & managed (.claude.json inside dir)
        var claude = Builtins.Get("claude");
        File.WriteAllText(Path.Combine(_home, ".claude.json"), """
            { "oauthAccount": { "emailAddress": "default@anthropic.example" } }
            """);
        var claudeManaged = _service.CreateManaged(claude, "Team");
        File.WriteAllText(Path.Combine(claudeManaged.Directory!, ".claude.json"), """
            { "oauthAccount": { "emailAddress": "team@anthropic.example" } }
            """);

        await _service.RefreshAllIdentitiesAsync(claude, TestContext.Current.CancellationToken);
        var claudeAccounts = _service.GetAccounts(claude);
        Assert.Equal("default@anthropic.example", claudeAccounts[0].Identity);
        Assert.Equal("team@anthropic.example", claudeAccounts[1].Identity);

        // 2. Codex auth.json JWT payload parsing (base64url without padding)
        var codex = Builtins.Get("codex");
        var codexManaged = _service.CreateManaged(codex, "OpenAI Pro");
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}""");
        var payload = Base64Url("""
            {
              "email": "builder@openai.example",
              "https://api.openai.com/auth": { "chatgpt_plan_type": "pro" }
            }
            """);
        var fakeJwt = $"{header}.{payload}.SECRET_SIGNATURE_DO_NOT_STORE";
        File.WriteAllText(Path.Combine(codexManaged.Directory!, "auth.json"), $$"""
            { "tokens": { "id_token": "{{fakeJwt}}", "access_token": "SECRET_ACCESS_TOKEN" } }
            """);

        var refreshedCodex = await _service.RefreshIdentityAsync(codex, codexManaged.Id, TestContext.Current.CancellationToken);
        Assert.Equal("builder@openai.example", refreshedCodex!.Identity);
        Assert.Equal("pro", refreshedCodex.PlanTier);

        var serializedStore = JsonSerializer.Serialize(_store.Current, OctopusJsonContext.FileOptions);
        Assert.DoesNotContain("SECRET_SIGNATURE", serializedStore);
        Assert.DoesNotContain("SECRET_ACCESS_TOKEN", serializedStore);

        // 3. Copilot config.json login parsing
        var copilot = Builtins.Get("copilot");
        var copilotManaged = _service.CreateManaged(copilot, "OSS");
        File.WriteAllText(Path.Combine(copilotManaged.Directory!, "config.json"), """
            { "last_logged_in_user": { "login": "octocat" } }
            """);
        var refreshedCopilot = await _service.RefreshIdentityAsync(copilot, copilotManaged.Id, TestContext.Current.CancellationToken);
        Assert.Equal("octocat", refreshedCopilot!.Identity);
    }

    [Fact]
    public async Task Global_keyring_captures_switches_and_restores_previous_credential_when_login_cancelled()
    {
        var agy = Builtins.Get("agy");
        var slot = new ExternalKeyringSlot("gemini", "antigravity", "gemini:antigravity", "username");
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await _service.HasLiveKeyringCredentialAsync(agy, ct));

        // Simulate existing Antigravity login in OS keychain
        await _secrets.SetExternalAsync(slot, "{\"refresh_token\":\"token-acct-1\"}", ct);
        Assert.True(await _service.HasLiveKeyringCredentialAsync(agy, ct));

        var acc1 = await _service.CaptureLiveKeyringAsync(agy, "Personal Google", cancellationToken: ct);
        Assert.Equal(acc1.Id, _service.GetActiveGlobalAccountId("agy"));
        Assert.Equal("{\"refresh_token\":\"token-acct-1\"}", await _secrets.GetAsync("Octopus/accounts/agy", acc1.Id, ct));

        // Prepare new login, user cancels (slot stays empty) -> previous credential & active id are restored!
        var cancelledPrep = await _service.PrepareNewKeyringLoginAsync(agy, ct);
        Assert.Null(await _secrets.GetExternalAsync(slot, ct));
        var cancelledResult = await _service.CompleteNewKeyringLoginAsync(agy, "Work Google", cancelledPrep, ct);
        Assert.Null(cancelledResult);
        Assert.Equal("{\"refresh_token\":\"token-acct-1\"}", await _secrets.GetExternalAsync(slot, ct));
        Assert.Equal(acc1.Id, _service.GetActiveGlobalAccountId("agy"));

        // Prepare new login, user completes OAuth -> new snapshot captured and marked active!
        var okPrep = await _service.PrepareNewKeyringLoginAsync(agy, ct);
        await _secrets.SetExternalAsync(slot, "{\"refresh_token\":\"token-acct-2\"}", ct);
        var acc2 = await _service.CompleteNewKeyringLoginAsync(agy, "Work Google", okPrep, ct);
        Assert.NotNull(acc2);
        Assert.Equal(acc2!.Id, _service.GetActiveGlobalAccountId("agy"));

        // Switch back to acc1 -> live keyring receives token-acct-1!
        await _service.SwitchGlobalKeyringAsync(agy, acc1.Id, ct);
        Assert.Equal("{\"refresh_token\":\"token-acct-1\"}", await _secrets.GetExternalAsync(slot, ct));
        Assert.Equal(acc1.Id, _service.GetActiveGlobalAccountId("agy"));

        // Removing acc1 deletes its snapshot from Octopus's secret store
        await _service.RemoveAsync(agy, acc1.Id, cancellationToken: ct);
        Assert.Null(await _secrets.GetAsync("Octopus/accounts/agy", acc1.Id, ct));
    }

    [Fact]
    public void Single_default_account_preserves_shell_api_keys_and_config_dir_for_third_party_apis()
    {
        var codex = Builtins.Get("codex");
        var claude = Builtins.Get("claude");

        // No additional managed/imported accounts exist: Resolve must NOT strip OPENAI_API_KEY / CODEX_HOME / ANTHROPIC_API_KEY.
        var codexResolved = _service.Resolve(codex, new AgentProfile());
        Assert.Equal(AgentAccountRecord.DefaultId, codexResolved.Account!.Id);
        Assert.Null(codexResolved.Environment);
        Assert.Null(codexResolved.UnsetEnvironment);

        var claudeResolved = _service.Resolve(claude, new AgentProfile());
        Assert.Equal(AgentAccountRecord.DefaultId, claudeResolved.Account!.Id);
        Assert.Null(claudeResolved.Environment);
        Assert.Null(claudeResolved.UnsetEnvironment);
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
