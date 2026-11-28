using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Acp.Process;
using Octopus.Acp.Protocol;
using Octopus.Acp.Transport;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.AgentGui;
using Octopus.App.Bots;
using Octopus.App.Collab;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Platform;
using Octopus.Remote.Host;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

public sealed class CollabCredentialsTests
{
    private static async Task<HttpStatusCode> PostPingStatusAsync(HttpClient http, string endpointUrl, string bearerToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var res = await http.SendAsync(req, ct);
        return res.StatusCode;
    }

    private static string ExtractBearerToken(AcpMcpServerConfig config)
    {
        Assert.True(config.Headers.TryGetValue("Authorization", out var header));
        Assert.StartsWith("Bearer ", header, StringComparison.Ordinal);
        return header["Bearer ".Length..];
    }

    [AvaloniaFact, FreshApp]
    public void Codex_cli_attachment_omits_bearer_token_from_arguments_and_uses_env_vars()
    {
        var server = AppHost.Get<CollabMcpServer>();
        Assert.True(server.Start());
        var injector = AppHost.Get<CollabCliMcpInjector>();
        var catalog = AppHost.Get<IAgentCatalog>();

        var tmpDir = Directory.CreateTempSubdirectory("octopus-sec04-codex-").FullName;
        try
        {
            var fakeForwarder = Path.Combine(tmpDir, "octopus-signal");
            File.WriteAllText(fakeForwarder, "");

            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Codex CLI" };
            var first = injector.Plan(catalog.Find("codex")!, item, fakeForwarder);
            Assert.NotNull(first);
            var firstToken = first!.Environment[CollabMcpServer.TokenEnvVariable];
            Assert.False(string.IsNullOrWhiteSpace(firstToken));

            Assert.DoesNotContain(first.Arguments, a => a.Contains(firstToken, StringComparison.Ordinal));
            Assert.DoesNotContain(first.Arguments, a => a.Contains("mcp_servers.octopus_collab.env=", StringComparison.Ordinal));
            Assert.Contains(first.Arguments, a => a.Contains("mcp_servers.octopus_collab.command=", StringComparison.Ordinal));
            Assert.Contains(first.Arguments, a => a == "mcp_servers.octopus_collab.args=[\"--mcp\"]");
            Assert.Contains(first.Arguments, a =>
                a.Contains("mcp_servers.octopus_collab.env_vars=", StringComparison.Ordinal)
                && a.Contains(CollabMcpServer.UrlEnvVariable, StringComparison.Ordinal)
                && a.Contains(CollabMcpServer.TokenEnvVariable, StringComparison.Ordinal)
                && a.Contains(CollabMcpServer.ItemIdEnvVariable, StringComparison.Ordinal));
            Assert.True(server.TryAuthenticateBearer($"Bearer {firstToken}", out var caller));
            Assert.Equal(item.Id, caller);

            // Re-planning rotates the token and invalidates the previous one.
            var second = injector.Plan(catalog.Find("codex")!, item, fakeForwarder);
            Assert.NotNull(second);
            var secondToken = second!.Environment[CollabMcpServer.TokenEnvVariable];
            Assert.NotEqual(firstToken, secondToken);
            Assert.False(server.TryAuthenticateBearer($"Bearer {firstToken}", out _));
            Assert.True(server.TryAuthenticateBearer($"Bearer {secondToken}", out _));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public void Claude_cli_attachment_omits_secret_from_config_file_and_enforces_private_storage()
    {
        var server = AppHost.Get<CollabMcpServer>();
        Assert.True(server.Start());
        var injector = AppHost.Get<CollabCliMcpInjector>();
        var catalog = AppHost.Get<IAgentCatalog>();

        var tmpDir = Directory.CreateTempSubdirectory("octopus-sec04-claude-").FullName;
        try
        {
            var fakeForwarder = Path.Combine(tmpDir, "octopus-signal");
            File.WriteAllText(fakeForwarder, "");

            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude CLI" };
            var attach = injector.Plan(catalog.Find("claude")!, item, fakeForwarder);
            Assert.NotNull(attach);
            Assert.Equal(2, attach!.Arguments.Count);
            Assert.Equal("--mcp-config", attach.Arguments[0]);

            var token = attach.Environment[CollabMcpServer.TokenEnvVariable];
            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.DoesNotContain(attach.Arguments, a => a.Contains(token, StringComparison.Ordinal));

            var cfgPath = attach.Arguments[1];
            Assert.True(File.Exists(cfgPath));
            var json = File.ReadAllText(cfgPath);
            Assert.DoesNotContain(token, json, StringComparison.Ordinal);

            var doc = JsonNode.Parse(json)!.AsObject();
            var envObj = doc["mcpServers"]![CollabMcpServer.ServerName]!["env"]!.AsObject();
            Assert.Equal($"${{{CollabMcpServer.UrlEnvVariable}}}", envObj[CollabMcpServer.UrlEnvVariable]!.GetValue<string>());
            Assert.Equal($"${{{CollabMcpServer.TokenEnvVariable}}}", envObj[CollabMcpServer.TokenEnvVariable]!.GetValue<string>());
            Assert.Equal($"${{{CollabMcpServer.ItemIdEnvVariable}}}", envObj[CollabMcpServer.ItemIdEnvVariable]!.GetValue<string>());

            if (!OperatingSystem.IsWindows())
            {
                var dir = Path.GetDirectoryName(cfgPath)!;
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(dir));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(cfgPath));
            }

            injector.CleanupItem(item.Id);
            Assert.False(File.Exists(cfgPath));
            Assert.False(server.TryAuthenticateBearer($"Bearer {token}", out _));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public void Unsupported_cli_agent_missing_forwarder_and_symlink_dir_fail_closed()
    {
        var server = AppHost.Get<CollabMcpServer>();
        Assert.True(server.Start());
        var injector = AppHost.Get<CollabCliMcpInjector>();
        var catalog = AppHost.Get<IAgentCatalog>();
        var paths = AppHost.Get<IAppPaths>();

        var tmpDir = Directory.CreateTempSubdirectory("octopus-sec04-failclosed-").FullName;
        try
        {
            var fakeForwarder = Path.Combine(tmpDir, "octopus-signal");
            File.WriteAllText(fakeForwarder, "");

            // Agents without a supported private CLI MCP attachment fail closed without issuing a token.
            var agyItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "agy", Title = "Agy CLI" };
            Assert.Null(injector.Plan(catalog.Find("agy")!, agyItem, fakeForwarder));

            // Supported agents fail closed when the forwarder executable is missing.
            var codexItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Codex CLI" };
            Assert.Null(injector.Plan(catalog.Find("codex")!, codexItem, Path.Combine(tmpDir, "missing-forwarder")));

            var claudeItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude CLI" };
            Assert.Null(injector.Plan(catalog.Find("claude")!, claudeItem, null));

            if (!OperatingSystem.IsWindows())
            {
                // A symlink at <cache>/collab-mcp must be rejected fail-closed.
                var decoyTarget = Path.Combine(tmpDir, "decoy");
                Directory.CreateDirectory(decoyTarget);
                Directory.CreateDirectory(paths.CacheDirectory);
                var collabDir = Path.Combine(paths.CacheDirectory, "collab-mcp");
                Directory.CreateSymbolicLink(collabDir, decoyTarget);

                Assert.Null(injector.Plan(catalog.Find("claude")!, claudeItem, fakeForwarder));
                Assert.Empty(Directory.GetFiles(decoyTarget));
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Gui_session_rotates_token_on_reconnect_keeps_while_detached_and_revokes_on_release()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-sec04-gui-").FullName;
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "OpenCode GUI", Surface = AgentSurface.Gui };
            projects.AddItem(project, item);

            var coordinator = AppHost.Get<ICollabCoordinator>();
            Assert.True(coordinator.Server.Start());

            var tabs = AppHost.Get<ITabContentRegistry>();
            var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
            var control = tabs.GetOrCreate(project, item);
            var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);

            await vm.ConnectAsync();
            var session1 = Assert.Single(fakeFactory.Created);
            var collab1 = Assert.Single(session1.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName);
            var token1 = ExtractBearerToken(collab1);

            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, coordinator.Server.EndpointUrl!, token1, ct));

            // Reconnect rotates the session credential and immediately revokes the prior token.
            await vm.RestartAsync();
            Assert.Equal(2, fakeFactory.Created.Count);
            var collab2 = Assert.Single(fakeFactory.Created[1].Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName);
            var token2 = ExtractBearerToken(collab2);
            Assert.NotEqual(token1, token2);
            Assert.Equal(HttpStatusCode.Unauthorized, await PostPingStatusAsync(http, coordinator.Server.EndpointUrl!, token1, ct));
            Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, coordinator.Server.EndpointUrl!, token2, ct));

            // Detaching the tab view keeps the background GUI session running, so its token remains valid.
            tabs.Detach(item.Id);
            Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, coordinator.Server.EndpointUrl!, token2, ct));

            // Releasing the detached tab disposes the GUI session and revokes its token even while the item remains in the project.
            await tabs.ReleaseAsync(item.Id);
            Assert.Equal(HttpStatusCode.Unauthorized, await PostPingStatusAsync(http, coordinator.Server.EndpointUrl!, token2, ct));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Project_removal_and_terminal_tab_release_revoke_tokens_and_clean_config_files()
    {
        var dir1 = Directory.CreateTempSubdirectory("octopus-sec04-proj1-").FullName;
        var dir2 = Directory.CreateTempSubdirectory("octopus-sec04-proj2-").FullName;
        var tmpFwd = Directory.CreateTempSubdirectory("octopus-sec04-fwd-").FullName;
        try
        {
            var fakeForwarder = Path.Combine(tmpFwd, "octopus-signal");
            File.WriteAllText(fakeForwarder, "");

            var coordinator = AppHost.Get<ICollabCoordinator>();
            var server = coordinator.Server;
            Assert.True(server.Start());
            var injector = AppHost.Get<CollabCliMcpInjector>();
            var catalog = AppHost.Get<IAgentCatalog>();
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            // 1. Removing a project revokes tokens and deletes Claude MCP config files for its items.
            var project1 = projects.AddProject(dir1);
            var item1 = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Proj1", Surface = AgentSurface.Terminal };
            projects.AddItem(project1, item1);
            var attach1 = injector.Plan(catalog.Find("claude")!, item1, fakeForwarder);
            Assert.NotNull(attach1);
            var token1 = attach1!.Environment[CollabMcpServer.TokenEnvVariable];
            var cfg1 = attach1.Arguments[1];
            Assert.True(File.Exists(cfg1));
            Assert.True(server.TryAuthenticateBearer($"Bearer {token1}", out _));

            projects.RemoveProject(project1.Id);
            Assert.False(server.TryAuthenticateBearer($"Bearer {token1}", out _));
            Assert.False(File.Exists(cfg1));

            // 2. Releasing a terminal tab directly (e.g. closing tab / app shutdown) revokes its token and removes its config file.
            var project2 = projects.AddProject(dir2);
            var item2 = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Proj2", Surface = AgentSurface.Terminal };
            projects.AddItem(project2, item2);
            _ = tabs.GetOrCreate(project2, item2);
            var attach2 = injector.Plan(catalog.Find("claude")!, item2, fakeForwarder);
            Assert.NotNull(attach2);
            var token2 = attach2!.Environment[CollabMcpServer.TokenEnvVariable];
            var cfg2 = attach2.Arguments[1];
            Assert.True(File.Exists(cfg2));
            Assert.True(server.TryAuthenticateBearer($"Bearer {token2}", out _));

            await tabs.ReleaseAsync(item2.Id);
            Assert.False(server.TryAuthenticateBearer($"Bearer {token2}", out _));
            Assert.False(File.Exists(cfg2));
        }
        finally
        {
            try { Directory.Delete(dir1, recursive: true); } catch (IOException) { }
            try { Directory.Delete(dir2, recursive: true); } catch (IOException) { }
            try { Directory.Delete(tmpFwd, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Bot_schedule_mcp_token_rotates_on_reconnect_and_revokes_when_chat_closes()
    {
        var ct = TestContext.Current.CancellationToken;
        var bots = AppHost.Get<IBotService>();
        var botMcp = AppHost.Get<BotScheduleMcpServer>();
        Assert.True(botMcp.Start());

        var bot = bots.CreateBot(new NewBotRequest("SecBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "Task Chat");
        Assert.NotNull(chat);

        var cfg1 = botMcp.BuildAcpServerConfig(chat!.Id);
        Assert.NotNull(cfg1);
        var token1 = ExtractBearerToken(cfg1!);

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, cfg1!.Url!, token1, ct));

        // Building a new session config rotates the token and revokes the prior one.
        var cfg2 = botMcp.BuildAcpServerConfig(chat.Id);
        Assert.NotNull(cfg2);
        var token2 = ExtractBearerToken(cfg2!);
        Assert.NotEqual(token1, token2);
        Assert.Equal(HttpStatusCode.Unauthorized, await PostPingStatusAsync(http, cfg1.Url!, token1, ct));
        Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, cfg2!.Url!, token2, ct));

        // Closing the bot chat revokes the active schedule MCP token.
        bots.CloseChat(bot.Id, chat.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, await PostPingStatusAsync(http, cfg2.Url!, token2, ct));
    }

    private sealed class NullSessionSink : ISessionSink
    {
        public void Output(ulong position, ReadOnlySpan<byte> bytes) { }
        public void Resized(ulong position, int columns, int rows) { }
        public void Reset(ulong position) { }
        public void Exited(int? exitCode) { }
    }

    private sealed class SimpleBridge(string itemId) : ICollabSessionBridge
    {
        public string ItemId => itemId;
        public string AgentId => "opencode";
        public string DisplayTitle => "Stub GUI";
        public string WorkingDirectory => "";
        public bool IsBusy => false;
        public bool IsConnected => true;
        public string? CurrentStatusDetail => null;
        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public string ReadRecentSummary(int maxTurns = 6) => "";
    }

    private sealed class TempTestPaths(string root) : IAppPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string CacheDirectory => Path.Combine(root, "cache");
    }

    private static HostWorkspace CreateHostWorkspace() => new(
        AppHost.Get<IProjectService>(),
        AppHost.Get<IItemLaunchService>(),
        AppHost.Get<ITerminalConnectionFactory>(),
        AppHost.Get<TerminalSessionRegistry>(),
        AppHost.Get<ITabContentRegistry>(),
        AppHost.Get<IItemActivityService>(),
        AppHost.Get<IAgentCatalog>(),
        AppHost.Get<IAgentScanner>(),
        AppHost.Get<ISettingsStore>(),
        AppHost.Get<IShellResolver>(),
        AppHost.Get<IFileTreeService>(),
        AppHost.Get<IGitService>(),
        AppHost.Get<PathComparer>(),
        bots: AppHost.Get<IBotService>());

    private static string? ExtractMcpConfigPath(LaunchSpec spec)
    {
        for (var i = 0; i < spec.Arguments.Count; i++)
        {
            if (spec.Arguments[i] == "--mcp-config" && i + 1 < spec.Arguments.Count)
            {
                return spec.Arguments[i + 1];
            }
            var arg = spec.Arguments[i];
            var idx = arg.IndexOf("--mcp-config", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rest = arg[(idx + "--mcp-config".Length)..].TrimStart();
                if (rest.Length == 0) continue;
                if (rest[0] is '\'' or '"')
                {
                    var quote = rest[0];
                    var end = rest.IndexOf(quote, 1);
                    if (end > 1) return rest[1..end];
                }
                var space = rest.IndexOfAny([' ', '\t', '\r', '\n']);
                return space > 0 ? rest[..space] : rest;
            }
        }
        return null;
    }

    [AvaloniaFact, FreshApp]
    public async Task Terminal_reattach_from_remote_and_local_tab_preserves_running_process_credential()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-sec04-reattach-").FullName;
        var tmpFwd = Directory.CreateTempSubdirectory("octopus-sec04-fwd-").FullName;
        var prevForwarder = Environment.GetEnvironmentVariable(ItemLaunchService.ForwarderVariable);
        Window? window = null;
        try
        {
            var fakeForwarder = Path.Combine(tmpFwd, "octopus-signal");
            File.WriteAllText(fakeForwarder, "");
            Environment.SetEnvironmentVariable(ItemLaunchService.ForwarderVariable, fakeForwarder);

            var settings = AppHost.Get<ISettingsStore>();
            settings.Update(s =>
            {
                s.Agents.Collab.Enabled = true;
                s.Agents.Collab.EnableTerminalBridge = true;
            });

            var coordinator = AppHost.Get<ICollabCoordinator>();
            var server = coordinator.Server;
            Assert.True(server.Start());

            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Term", Surface = AgentSurface.Terminal };
            projects.AddItem(project, item);

            var fakeTerminals = AppHost.Get<FakeTerminalConnectionFactory>();
            var sessions = AppHost.Get<TerminalSessionRegistry>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            using var host = CreateHostWorkspace();

            // 1. First remote viewer starts the terminal process and mints its credential bound to the PTY link generation.
            using var attach1 = await host.AttachAsync(item.Id, 80, 24, nativeSnapshot: true, claimSize: false, new NullSessionSink());
            Assert.Single(fakeTerminals.Started);
            var session = sessions.Find(item.Id);
            Assert.NotNull(session);
            Assert.Equal(session!.CurrentLinkId, fakeTerminals.Started[0].Environment["OCTOPUS_SIGNAL_LINK_ID"]);
            var token1 = fakeTerminals.Started[0].Environment[CollabMcpServer.TokenEnvVariable];
            var cfg1 = ExtractMcpConfigPath(fakeTerminals.Started[0]);
            Assert.False(string.IsNullOrWhiteSpace(token1));
            Assert.NotNull(cfg1);
            Assert.True(File.Exists(cfg1));
            Assert.True(server.TryAuthenticateBearer($"Bearer {token1}", out var caller1));
            Assert.Equal(item.Id, caller1);

            // 2. Second remote viewer reattaches to the running session without starting a new process or invalidating token1.
            using var attach2 = await host.AttachAsync(item.Id, 100, 30, nativeSnapshot: true, claimSize: false, new NullSessionSink());
            Assert.Single(fakeTerminals.Started);
            Assert.True(server.TryAuthenticateBearer($"Bearer {token1}", out _), "Reattaching a second remote viewer invalidated the running process's token.");
            Assert.True(File.Exists(cfg1));

            // 3. First local tab view attaches to the already-running remote session without invalidating token1.
            var tab = tabs.GetOrCreate(project, item);
            window = new Window { Width = 700, Height = 450, Content = tab };
            window.Show();
            Frames.Pump();
            Assert.Single(fakeTerminals.Started);
            Assert.True(server.TryAuthenticateBearer($"Bearer {token1}", out _), "Opening the local tab view invalidated the running process's token.");
            Assert.True(File.Exists(cfg1));

            await tabs.ReleaseAsync(item.Id);
            Assert.False(server.TryAuthenticateBearer($"Bearer {token1}", out _));
            Assert.False(File.Exists(cfg1));
        }
        finally
        {
            window?.Close();
            Environment.SetEnvironmentVariable(ItemLaunchService.ForwarderVariable, prevForwarder);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            try { Directory.Delete(tmpFwd, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Terminal_exit_restart_failed_start_and_headless_release_manage_credential_generations()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-sec04-term-life-").FullName;
        var tmpFwd = Directory.CreateTempSubdirectory("octopus-sec04-fwd-").FullName;
        var prevForwarder = Environment.GetEnvironmentVariable(ItemLaunchService.ForwarderVariable);
        try
        {
            var fakeForwarder = Path.Combine(tmpFwd, "octopus-signal");
            File.WriteAllText(fakeForwarder, "");
            Environment.SetEnvironmentVariable(ItemLaunchService.ForwarderVariable, fakeForwarder);

            var settings = AppHost.Get<ISettingsStore>();
            settings.Update(s =>
            {
                s.Agents.Collab.Enabled = true;
                s.Agents.Collab.EnableTerminalBridge = true;
            });

            var server = AppHost.Get<ICollabCoordinator>().Server;
            Assert.True(server.Start());

            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Term", Surface = AgentSurface.Terminal };
            projects.AddItem(project, item);

            var fakeTerminals = AppHost.Get<FakeTerminalConnectionFactory>();
            var sessions = AppHost.Get<TerminalSessionRegistry>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            using var host = CreateHostWorkspace();

            // 1. Start headless terminal and verify token1 + config1 + PTY link generation.
            using var attach = await host.AttachAsync(item.Id, 80, 24, nativeSnapshot: true, claimSize: false, new NullSessionSink());
            Assert.Single(fakeTerminals.Started);
            var session = sessions.Find(item.Id)!;
            var linkId1 = session.CurrentLinkId;
            Assert.False(string.IsNullOrWhiteSpace(linkId1));
            Assert.Equal(linkId1, fakeTerminals.Started[0].Environment["OCTOPUS_SIGNAL_LINK_ID"]);
            var token1 = fakeTerminals.Started[0].Environment[CollabMcpServer.TokenEnvVariable];
            var cfg1 = ExtractMcpConfigPath(fakeTerminals.Started[0]);
            Assert.NotNull(cfg1);
            Assert.True(File.Exists(cfg1));
            Assert.True(server.TryAuthenticateBearer($"Bearer {token1}", out _));

            // 2. Process exit immediately revokes token1 and deletes config1.
            fakeTerminals.Connections[0].Exit(0);
            Assert.True(Frames.Until(() => !session.IsRunning, milliseconds: 3000));
            Assert.False(server.TryAuthenticateBearer($"Bearer {token1}", out _), "Exited terminal process still had a valid credential.");
            Assert.False(File.Exists(cfg1), "Exited terminal process left its MCP config file on disk.");

            // 3. Restart mints a fresh credential for the replacement process generation (does not reuse exited/revoked spec).
            await host.RestartAsync(item.Id);
            Assert.Equal(2, fakeTerminals.Started.Count);
            var linkId2 = session.CurrentLinkId;
            Assert.NotEqual(linkId1, linkId2);
            Assert.Equal(linkId2, fakeTerminals.Started[1].Environment["OCTOPUS_SIGNAL_LINK_ID"]);
            var token2 = fakeTerminals.Started[1].Environment[CollabMcpServer.TokenEnvVariable];
            var cfg2 = ExtractMcpConfigPath(fakeTerminals.Started[1]);
            Assert.NotEqual(token1, token2);
            Assert.False(server.TryAuthenticateBearer($"Bearer {token1}", out _));
            Assert.True(server.TryAuthenticateBearer($"Bearer {token2}", out var caller2));
            Assert.Equal(item.Id, caller2);
            Assert.NotNull(cfg2);
            Assert.True(File.Exists(cfg2));

            // 4. Failed restart revokes both the previous and failed-attempt credentials.
            string? failedToken = null;
            string? failedCfg = null;
            fakeTerminals.StartOverride = (spec, _, _) =>
            {
                failedToken = spec.Environment[CollabMcpServer.TokenEnvVariable];
                failedCfg = ExtractMcpConfigPath(spec);
                throw new IOException("simulated pty spawn failure");
            };
            await Assert.ThrowsAsync<IOException>(() => host.RestartAsync(item.Id));
            fakeTerminals.StartOverride = null;
            Assert.False(server.TryAuthenticateBearer($"Bearer {token2}", out _), "Previous token remained valid after restart stopped the old process.");
            Assert.False(File.Exists(cfg2));
            Assert.NotNull(failedToken);
            Assert.False(server.TryAuthenticateBearer($"Bearer {failedToken}", out _), "Failed restart attempt left a valid credential.");
            Assert.False(File.Exists(failedCfg));

            // 5. Failed initial launch on a second item leaves no valid token or config file.
            var failingItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Failing Term", Surface = AgentSurface.Terminal };
            projects.AddItem(project, failingItem);
            string? failInitToken = null;
            string? failInitCfg = null;
            fakeTerminals.StartOverride = (spec, _, _) =>
            {
                failInitToken = spec.Environment[CollabMcpServer.TokenEnvVariable];
                failInitCfg = ExtractMcpConfigPath(spec);
                throw new IOException("simulated initial spawn failure");
            };
            await Assert.ThrowsAsync<Octopus.Remote.Protocol.RemoteException>(() =>
                host.AttachAsync(failingItem.Id, 80, 24, nativeSnapshot: true, claimSize: false, new NullSessionSink()));
            fakeTerminals.StartOverride = null;
            Assert.NotNull(failInitToken);
            Assert.False(server.TryAuthenticateBearer($"Bearer {failInitToken}", out _), "Failed initial launch left a valid credential.");
            Assert.False(File.Exists(failInitCfg));

            // 6. Bulk ReleaseAllAsync on a never-viewed headless session revokes its token and removes its config immediately.
            var headlessItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Headless Term", Surface = AgentSurface.Terminal };
            projects.AddItem(project, headlessItem);
            using var headlessAttach = await host.AttachAsync(headlessItem.Id, 80, 24, nativeSnapshot: true, claimSize: false, new NullSessionSink());
            var headlessSpec = fakeTerminals.Started[^1];
            var headlessToken = headlessSpec.Environment[CollabMcpServer.TokenEnvVariable];
            var headlessCfg = ExtractMcpConfigPath(headlessSpec);
            Assert.True(server.TryAuthenticateBearer($"Bearer {headlessToken}", out _));
            Assert.True(File.Exists(headlessCfg));

            await tabs.ReleaseAllAsync();
            Assert.False(server.TryAuthenticateBearer($"Bearer {headlessToken}", out _), "Headless session token survived ReleaseAllAsync.");
            Assert.False(File.Exists(headlessCfg), "Headless session config file survived ReleaseAllAsync.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ItemLaunchService.ForwarderVariable, prevForwarder);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            try { Directory.Delete(tmpFwd, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Stale_tab_close_and_gui_unregister_preserve_replacement_and_other_surface_credentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-sec04-stale-").FullName;
        var tmpFwd = Directory.CreateTempSubdirectory("octopus-sec04-fwd-").FullName;
        try
        {
            var fakeForwarder = Path.Combine(tmpFwd, "octopus-signal");
            File.WriteAllText(fakeForwarder, "");

            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "OpenCode GUI", Surface = AgentSurface.Gui };
            projects.AddItem(project, item);

            var coordinator = AppHost.Get<ICollabCoordinator>();
            var server = coordinator.Server;
            Assert.True(server.Start());

            var tabs = AppHost.Get<ITabContentRegistry>();
            var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
            var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // 1. Start generation 1 GUI tab whose close awaits disposal.
            fakeFactory.ConfigureNext = s =>
            {
                s.DisposeHandler = async _ =>
                {
                    disposeStarted.TrySetResult();
                    await disposeGate.Task;
                };
            };
            var control1 = tabs.GetOrCreate(project, item);
            var vm1 = Assert.IsType<AgentGuiTabViewModel>(control1.DataContext);
            await vm1.ConnectAsync();
            var token1 = ExtractBearerToken(Assert.Single(fakeFactory.Created[0].Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName));
            Assert.True(server.TryAuthenticateBearer($"Bearer {token1}", out _));

            // 2. Begin closing generation 1; while disposal is pending, create and connect generation 2.
            var release1 = tabs.ReleaseAsync(item.Id);
            await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            fakeFactory.ConfigureNext = null;
            var control2 = tabs.GetOrCreate(project, item);
            var vm2 = Assert.IsType<AgentGuiTabViewModel>(control2.DataContext);
            await vm2.ConnectAsync();
            var token2 = ExtractBearerToken(Assert.Single(fakeFactory.Created[1].Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName));
            Assert.NotEqual(token1, token2);
            Assert.True(server.TryAuthenticateBearer($"Bearer {token2}", out _));

            // 3. Complete stale close of generation 1: token1 must be revoked, token2 must remain valid.
            disposeGate.TrySetResult();
            await release1;
            Assert.False(server.TryAuthenticateBearer($"Bearer {token1}", out _));
            Assert.True(server.TryAuthenticateBearer($"Bearer {token2}", out var caller2), "Stale close of generation 1 revoked replacement generation 2's credential.");
            Assert.Equal(item.Id, caller2);

            // 4. Cross-surface isolation: unregistering a GUI session must not remove an active terminal config/credential on the same item.
            var injector = AppHost.Get<CollabCliMcpInjector>();
            var catalog = AppHost.Get<IAgentCatalog>();
            var termItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Dual Surface", Surface = AgentSurface.Terminal };
            projects.AddItem(project, termItem);
            var cliAttach = injector.Plan(catalog.Find("claude")!, termItem, fakeForwarder);
            Assert.NotNull(cliAttach);
            var termToken = cliAttach!.Environment[CollabMcpServer.TokenEnvVariable];
            var termCfg = cliAttach.Arguments[1];
            Assert.True(File.Exists(termCfg));
            Assert.True(server.TryAuthenticateBearer($"Bearer {termToken}", out _));

            var stubGui = new SimpleBridge(termItem.Id);
            coordinator.RegisterGuiSession(stubGui);
            coordinator.UnregisterGuiSession(termItem.Id);
            Assert.True(server.TryAuthenticateBearer($"Bearer {termToken}", out _), "Unregistering a GUI session revoked the terminal credential.");
            Assert.True(File.Exists(termCfg), "Unregistering a GUI session deleted the terminal MCP config file.");

            // Whole-item removal still revokes everything for the item.
            projects.RemoveItem(termItem.Id);
            Assert.False(server.TryAuthenticateBearer($"Bearer {termToken}", out _));
            Assert.False(File.Exists(termCfg));

            await tabs.ReleaseAsync(item.Id);
            Assert.False(server.TryAuthenticateBearer($"Bearer {token2}", out _));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            try { Directory.Delete(tmpFwd, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Acp_disconnect_resolver_failure_start_failure_and_closed_event_revoke_credentials()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-sec04-acp-fail-").FullName;
        var adapterDir = Directory.CreateTempSubdirectory("octopus-sec04-adapter-").FullName;
        try
        {
            var settings = AppHost.Get<ISettingsStore>();
            settings.Update(s => s.Agents.AcpAdapterPolicy = AcpAdapterPolicy.NativeOnly);

            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "OpenCode ACP", Surface = AgentSurface.Gui };
            projects.AddItem(project, item);

            var coordinator = AppHost.Get<ICollabCoordinator>();
            var server = coordinator.Server;
            Assert.True(server.Start());

            var tabs = AppHost.Get<ITabContentRegistry>();
            var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
            var control = tabs.GetOrCreate(project, item);
            var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);

            // 1. Unexpected AcpClosedEvent revokes the session's credential.
            await vm.ConnectAsync();
            var session1 = Assert.Single(fakeFactory.Created);
            var token1 = ExtractBearerToken(Assert.Single(session1.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName));
            Assert.True(server.TryAuthenticateBearer($"Bearer {token1}", out _));

            session1.Emit(new AcpClosedEvent(new IOException("ACP adapter exited")));
            Assert.False(server.TryAuthenticateBearer($"Bearer {token1}", out _), "AcpClosedEvent left the dead session's credential valid.");

            // 2. Startup error after credential issuance revokes the newly minted credential.
            fakeFactory.ConfigureNext = s =>
            {
                s.StartAsyncHandler = (_, _, _) => Task.FromException<AcpSessionStartResult>(new IOException("handshake failed"));
            };
            await vm.ConnectAsync();
            fakeFactory.ConfigureNext = null;
            Assert.Equal(2, fakeFactory.Created.Count);
            var token2 = ExtractBearerToken(Assert.Single(fakeFactory.Created[1].Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName));
            Assert.False(server.TryAuthenticateBearer($"Bearer {token2}", out _), "Failed ACP StartAsync left the newly issued credential valid.");

            // 3. Auth-required failure after credential issuance also revokes the newly minted credential.
            fakeFactory.ConfigureNext = s =>
            {
                s.StartAsyncHandler = (_, _, _) => Task.FromException<AcpSessionStartResult>(new AcpAuthRequiredException("auth required", []));
            };
            await vm.ConnectAsync();
            fakeFactory.ConfigureNext = null;
            Assert.Equal(3, fakeFactory.Created.Count);
            var token3 = ExtractBearerToken(Assert.Single(fakeFactory.Created[2].Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName));
            Assert.False(server.TryAuthenticateBearer($"Bearer {token3}", out _), "Auth-required ACP StartAsync left the newly issued credential valid.");

            // 4. Disconnect + resolver failure during reconnect revokes the previous session's credential.
            // Use a synthetic adapter-mode agent in NativeOnly mode so deleting the cached inert dummy file fails offline on any host.
            var userAgentsDir = Path.Combine(AppHost.Get<IAppPaths>().DataDirectory, "agents");
            Directory.CreateDirectory(userAgentsDir);
            File.WriteAllText(
                Path.Combine(userAgentsDir, "synthetic-acp-test.json"),
                """
                {
                  "id": "synthetic-acp-test",
                  "name": "Synthetic ACP Test",
                  "acp": {
                    "mode": "adapter",
                    "dialect": "generic",
                    "adapter": {
                      "executables": ["synthetic-nonexistent-acp-adapter-bin"]
                    }
                  }
                }
                """);
            AppHost.Get<IAgentCatalog>().Reload();

            var syntheticItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "synthetic-acp-test", Title = "Synthetic ACP", Surface = AgentSurface.Gui };
            projects.AddItem(project, syntheticItem);
            var adapterInstaller = AppHost.Get<AcpAdapterInstaller>();
            var cachedBinDir = Path.Combine(adapterInstaller.AdapterRoot("synthetic-acp-test"), "npm", "node_modules", ".bin");
            Directory.CreateDirectory(cachedBinDir);
            var fakeAdapterExe = Path.Combine(cachedBinDir, OperatingSystem.IsWindows() ? "synthetic-nonexistent-acp-adapter-bin.cmd" : "synthetic-nonexistent-acp-adapter-bin");
            File.WriteAllText(fakeAdapterExe, "");

            var syntheticControl = tabs.GetOrCreate(project, syntheticItem);
            var syntheticVm = Assert.IsType<AgentGuiTabViewModel>(syntheticControl.DataContext);
            await syntheticVm.ConnectAsync();
            var syntheticSession = fakeFactory.Created[^1];
            var syntheticToken = ExtractBearerToken(Assert.Single(syntheticSession.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName));
            Assert.True(server.TryAuthenticateBearer($"Bearer {syntheticToken}", out _));

            // Remove the inert dummy adapter so resolver throws offline during RestartAsync.
            File.Delete(fakeAdapterExe);
            await syntheticVm.RestartAsync();
            Assert.Equal(AgentGuiState.Error, syntheticVm.State);
            Assert.False(server.TryAuthenticateBearer($"Bearer {syntheticToken}", out _), "Resolver failure during reconnect left the previous session's credential valid.");

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            try { Directory.Delete(adapterDir, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Closed_bot_chat_rejects_credential_issuance_tool_dispatch_and_stale_connect()
    {
        var ct = TestContext.Current.CancellationToken;
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);
        var bots = AppHost.Get<IBotService>();
        var botMcp = AppHost.Get<BotScheduleMcpServer>();
        Assert.True(botMcp.Start());
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        var bot = bots.CreateBot(new NewBotRequest("SecBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "Task Chat");
        Assert.NotNull(chat);

        var cfgOpen = botMcp.BuildAcpServerConfig(chat!.Id);
        Assert.NotNull(cfgOpen);
        var openToken = ExtractBearerToken(cfgOpen!);

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, cfgOpen!.Url!, openToken, ct));

        // 1. Closing the chat revokes openToken, rejects new issuance, and rejects direct tool dispatch.
        bots.CloseChat(bot.Id, chat.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, await PostPingStatusAsync(http, cfgOpen.Url!, openToken, ct));
        Assert.Null(botMcp.BuildAcpServerConfig(chat.Id));
        var closedToolResult = await botMcp.CallOnUiAsync(chat.Id, "schedule_list", null);
        Assert.True(closedToolResult["isError"]?.GetValue<bool>() == true, "Closed bot chat was still allowed to invoke schedule tools.");

        // 2. Reopening the chat restores issuance and tool dispatch, while closing during an in-flight connect revokes stale credentials.
        bots.OpenChat(bot.Id, chat.Id);
        var backing = bots.GetBackingProject(bots.Bots.First(b => b.Id == bot.Id));
        var backingItem = Assert.Single(backing.Items, i => i.Id == chat.Id);
        var control = tabs.GetOrCreate(backing, backingItem);
        var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);

        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeFactory.ConfigureNext = s =>
        {
            s.StartAsyncHandler = async (self, _, token) =>
            {
                startEntered.TrySetResult();
                await releaseStart.Task.WaitAsync(token);
                return new AcpSessionStartResult { Kind = "new", SessionId = self.SessionId ?? "bot-s1" };
            };
        };

        var connectTask = vm.ConnectAsync();
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var inFlightSession = Assert.Single(fakeFactory.Created);
        var inFlightBotCfg = Assert.Single(inFlightSession.Callbacks.McpServers, s => s.Name == BotScheduleMcpServer.ServerName);
        var inFlightToken = ExtractBearerToken(inFlightBotCfg);

        bots.CloseChat(bot.Id, chat.Id);
        var releaseTask = tabs.ReleaseAsync(backingItem.Id);
        releaseStart.TrySetResult();
        await Task.WhenAll(connectTask, releaseTask);

        Assert.Equal(HttpStatusCode.Unauthorized, await PostPingStatusAsync(http, inFlightBotCfg.Url!, inFlightToken, ct));
        Assert.Null(botMcp.BuildAcpServerConfig(chat.Id));
    }

    [AvaloniaFact, FreshApp]
    public void Shutdown_fences_late_credential_issuance_and_config_creation()
    {
        var tmpRoot = Directory.CreateTempSubdirectory("octopus-sec04-shutdown-").FullName;
        try
        {
            var fakeForwarder = Path.Combine(tmpRoot, "octopus-signal");
            File.WriteAllText(fakeForwarder, "");

            var paths = new TempTestPaths(tmpRoot);
            var server = new CollabMcpServer();
            Assert.True(server.Start());
            var injector = new CollabCliMcpInjector(server, paths);
            var catalog = AppHost.Get<IAgentCatalog>();

            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Shutdown Claude", Surface = AgentSurface.Terminal };
            var attach = injector.Plan(catalog.Find("claude")!, item, fakeForwarder);
            Assert.NotNull(attach);
            var token = attach!.Environment[CollabMcpServer.TokenEnvVariable];
            var cfg = attach.Arguments[1];
            Assert.True(File.Exists(cfg));
            Assert.True(server.TryAuthenticateBearer($"Bearer {token}", out _));

            // Dispose injector first while server is still running: late Plan must be fenced and leave no config file.
            injector.Dispose();
            Assert.False(File.Exists(cfg));
            Assert.False(server.TryAuthenticateBearer($"Bearer {token}", out _));

            var lateAttach = injector.Plan(catalog.Find("claude")!, item, fakeForwarder);
            Assert.Null(lateAttach);
            var collabDir = Path.Combine(paths.CacheDirectory, "collab-mcp");
            if (Directory.Exists(collabDir))
            {
                Assert.Empty(Directory.GetFiles(collabDir));
            }

            // Dispose server: late BuildAcpServerConfig must return null and authenticate nothing.
            server.Dispose();
            Assert.Null(server.BuildAcpServerConfig(item.Id, fakeForwarder));
            Assert.False(server.TryAuthenticateBearer($"Bearer {token}", out _));
        }
        finally
        {
            try { Directory.Delete(tmpRoot, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Deferred_bot_close_and_reopen_preserves_reopened_owner_across_dispatcher_and_host_boundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);
        var bots = AppHost.Get<IBotService>();
        var botMcp = AppHost.Get<BotScheduleMcpServer>();
        var collab = AppHost.Get<ICollabCoordinator>();
        Assert.True(botMcp.Start());
        Assert.True(collab.Server.Start());
        var tabs = AppHost.Get<ITabContentRegistry>();
        var dialogs = AppHost.Get<IDialogService>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        var bot = bots.CreateBot(new NewBotRequest("SecBotDeferred", "opencode", "#2f9e63", null, null));
        var chat1 = bots.CreateChat(bot.Id, "opencode", "Deferred Chat");
        Assert.NotNull(chat1);

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var dock = new BotDockController(bot, bots, tabs, new CloseConfirmation(tabs, dialogs), new Border());

        // 1. BotDockController close -> reopen before background dispatcher callback runs.
        var backing = bots.GetBackingProject(bots.Bots.First(b => b.Id == bot.Id));
        var item1 = Assert.Single(backing.Items, i => i.Id == chat1!.Id);
        var control1 = tabs.GetOrCreate(backing, item1);
        var vm1 = Assert.IsType<AgentGuiTabViewModel>(control1.DataContext);
        await vm1.ConnectAsync();
        var session1 = fakeFactory.Created[^1];
        var botCfg1 = Assert.Single(session1.Callbacks.McpServers, s => s.Name == BotScheduleMcpServer.ServerName);
        var collabCfg1 = Assert.Single(session1.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName);
        var botToken1 = ExtractBearerToken(botCfg1);
        var collabToken1 = ExtractBearerToken(collabCfg1);
        Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, botCfg1.Url!, botToken1, ct));
        Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, collabCfg1.Url!, collabToken1, ct));

        // Close chat (posts background release), then immediately reopen BEFORE background dispatcher jobs run.
        bots.CloseChat(bot.Id, chat1!.Id);
        bots.OpenChat(bot.Id, chat1.Id);

        var reopenedItem1 = Assert.Single(backing.Items, i => i.Id == chat1.Id);
        var control2 = tabs.GetOrCreate(backing, reopenedItem1);
        Assert.NotSame(control1, control2);
        var vm2 = Assert.IsType<AgentGuiTabViewModel>(control2.DataContext);
        await vm2.ConnectAsync();
        var session2 = fakeFactory.Created[^1];
        var botCfg2 = Assert.Single(session2.Callbacks.McpServers, s => s.Name == BotScheduleMcpServer.ServerName);
        var collabCfg2 = Assert.Single(session2.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName);
        var botToken2 = ExtractBearerToken(botCfg2);
        var collabToken2 = ExtractBearerToken(collabCfg2);

        // Now flush the deferred background close callback from BotDockController.
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Frames.Pump();

        Assert.Same(control2, tabs.Find(chat1.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostPingStatusAsync(http, botCfg1.Url!, botToken1, ct));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostPingStatusAsync(http, collabCfg1.Url!, collabToken1, ct));
        Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, botCfg2.Url!, botToken2, ct));
        Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, collabCfg2.Url!, collabToken2, ct));

        // 2. HostWorkspace.CloseBotChatAsync when chat reopens across the close/await boundary.
        var chat2 = bots.CreateChat(bot.Id, "opencode", "Host Deferred Chat");
        Assert.NotNull(chat2);
        var itemA = Assert.Single(backing.Items, i => i.Id == chat2!.Id);
        var controlA = tabs.GetOrCreate(backing, itemA);
        var vmA = Assert.IsType<AgentGuiTabViewModel>(controlA.DataContext);
        await vmA.ConnectAsync();
        var sessionA = fakeFactory.Created[^1];
        var botCfgA = Assert.Single(sessionA.Callbacks.McpServers, s => s.Name == BotScheduleMcpServer.ServerName);
        var collabCfgA = Assert.Single(sessionA.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName);
        var botTokenA = ExtractBearerToken(botCfgA);
        var collabTokenA = ExtractBearerToken(collabCfgA);

        Control? controlB = null;
        var reopenedOnce = false;
        void OnBotsChangedReopen(object? sender, BotChangedEventArgs e)
        {
            if (reopenedOnce || e.Kind != BotChangeKind.Chats || e.Chat?.Id != chat2!.Id || e.Chat.IsOpen)
            {
                return;
            }
            reopenedOnce = true;
            bots.OpenChat(bot.Id, chat2.Id);
            var reopenedItem = backing.Items.First(i => i.Id == chat2.Id);
            controlB = tabs.GetOrCreate(backing, reopenedItem);
        }

        using var host = CreateHostWorkspace();
        bots.Changed += OnBotsChangedReopen;
        try
        {
            await host.CloseBotChatAsync(bot.Id, chat2!.Id);
        }
        finally
        {
            bots.Changed -= OnBotsChangedReopen;
        }
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Frames.Pump();

        Assert.True(reopenedOnce);
        Assert.NotNull(controlB);
        Assert.NotSame(controlA, controlB);
        Assert.Same(controlB, tabs.Find(chat2!.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostPingStatusAsync(http, botCfgA.Url!, botTokenA, ct));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostPingStatusAsync(http, collabCfgA.Url!, collabTokenA, ct));

        var vmB = Assert.IsType<AgentGuiTabViewModel>(controlB!.DataContext);
        await vmB.ConnectAsync();
        var sessionB = fakeFactory.Created[^1];
        var botCfgB = Assert.Single(sessionB.Callbacks.McpServers, s => s.Name == BotScheduleMcpServer.ServerName);
        var collabCfgB = Assert.Single(sessionB.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName);
        Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, botCfgB.Url!, ExtractBearerToken(botCfgB), ct));
        Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, collabCfgB.Url!, ExtractBearerToken(collabCfgB), ct));

        await tabs.ReleaseAllAsync();
    }

    [AvaloniaFact, FreshApp]
    public async Task Bot_dock_controller_deferred_close_via_fake_dispatcher_preserves_reopened_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);
        var bots = AppHost.Get<IBotService>();
        var botMcp = AppHost.Get<BotScheduleMcpServer>();
        var collab = AppHost.Get<ICollabCoordinator>();
        Assert.True(botMcp.Start());
        Assert.True(collab.Server.Start());
        var tabs = AppHost.Get<ITabContentRegistry>();
        var dialogs = AppHost.Get<IDialogService>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        var bot = bots.CreateBot(new NewBotRequest("SecBotFakeDispatch", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "Queued Close Chat");
        Assert.NotNull(chat);

        var fakeQueue = new Queue<Action>();
        using var dock = new BotDockController(
            bot,
            bots,
            tabs,
            new CloseConfirmation(tabs, dialogs),
            new Border(),
            postBackground: fakeQueue.Enqueue);

        var backing = bots.GetBackingProject(bots.Bots.First(b => b.Id == bot.Id));
        var item1 = Assert.Single(backing.Items, i => i.Id == chat!.Id);
        var control1 = tabs.GetOrCreate(backing, item1);
        var vm1 = Assert.IsType<AgentGuiTabViewModel>(control1.DataContext);
        await vm1.ConnectAsync();
        var session1 = fakeFactory.Created[^1];
        var collabToken1 = ExtractBearerToken(Assert.Single(session1.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName));

        // Closing queues a deferred release in the fake UI dispatcher without executing it yet.
        bots.CloseChat(bot.Id, chat!.Id);
        Assert.Single(fakeQueue);

        // Reopening before the fake dispatcher drains creates a distinct replacement owner.
        bots.OpenChat(bot.Id, chat.Id);
        var item2 = Assert.Single(backing.Items, i => i.Id == chat.Id);
        var control2 = tabs.GetOrCreate(backing, item2);
        Assert.NotSame(control1, control2);
        var vm2 = Assert.IsType<AgentGuiTabViewModel>(control2.DataContext);
        await vm2.ConnectAsync();
        var session2 = fakeFactory.Created[^1];
        var botCfg2 = Assert.Single(session2.Callbacks.McpServers, s => s.Name == BotScheduleMcpServer.ServerName);
        var collabCfg2 = Assert.Single(session2.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName);

        // Execute the queued deferred close callback from the fake UI dispatcher.
        while (fakeQueue.Count > 0)
        {
            fakeQueue.Dequeue()();
        }
        Frames.Pump();

        Assert.Same(control2, tabs.Find(chat.Id));
        Assert.False(collab.Server.TryAuthenticateBearer($"Bearer {collabToken1}", out _));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, botCfg2.Url!, ExtractBearerToken(botCfg2), ct));
        Assert.Equal(HttpStatusCode.OK, await PostPingStatusAsync(http, collabCfg2.Url!, ExtractBearerToken(collabCfg2), ct));

        await tabs.ReleaseAllAsync();
    }
}
