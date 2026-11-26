using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Octopus.App.Bots;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Sidebar;
using Octopus.Core.Agents;
using Octopus.Core.Bots;
using Octopus.Platform;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using Xunit;

namespace Octopus.App.Tests;

public sealed class BotsTests
{
    [AvaloniaFact]
    public void CreateBot_CreatesHomeDirectoryWithOwnershipSplit()
    {
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("西瓜", "claude", "#2f9e63", "pr", "盯住 PR 与 CI"));

        var homePath = bots.GetHomePath(bot);
        Assert.True(Directory.Exists(homePath));
        Assert.True(Directory.Exists(Path.Combine(homePath, "memory")));
        Assert.True(Directory.Exists(Path.Combine(homePath, "skills")));
        Assert.True(File.Exists(bots.Home.GetSoulPath(bot)));
        Assert.True(File.Exists(bots.Home.GetMemoryPath(bot)));
        Assert.True(File.Exists(bots.Home.GetAgentsPath(bot)));
        Assert.True(File.Exists(bots.Home.GetClaudePath(bot)));
        Assert.True(File.Exists(bots.Home.GetManifestPath(bot)));
        Assert.Equal("@AGENTS.md", File.ReadAllText(bots.Home.GetClaudePath(bot)).Trim());

        // User edits to SOUL.md and MEMORY.md must survive managed mirror syncs.
        File.WriteAllText(bots.Home.GetSoulPath(bot), "# Custom Soul\nKeep everything concise.");
        File.WriteAllText(bots.Home.GetMemoryPath(bot), "# Memory\n- Prefers short PR summaries");

        bots.AddConnector(bot.Id, "github");

        Assert.Contains("Custom Soul", File.ReadAllText(bots.Home.GetSoulPath(bot)));
        Assert.Contains("Prefers short PR summaries", File.ReadAllText(bots.Home.GetMemoryPath(bot)));
        Assert.Contains("GitHub", File.ReadAllText(bots.Home.GetAgentsPath(bot)));
        Assert.Contains("GitHub", File.ReadAllText(bots.Home.GetManifestPath(bot)));
    }

    [AvaloniaFact]
    public void RailEntry_SwitchesBetweenBotModeAndProjectMode()
    {
        var projects = AppHost.Get<IProjectService>();
        var bots = AppHost.Get<IBotService>();
        var shell = AppHost.Get<ShellViewModel>();

        var tempDir = Directory.CreateTempSubdirectory("octopus-bot-proj-").FullName;
        try
        {
            var project = projects.AddProject(tempDir, "Demo");
            Assert.False(shell.IsBotMode);
            Assert.False(shell.Rail.IsBotMode);

            shell.Rail.EnterBotModeCommand.Execute(null);
            Assert.True(bots.IsBotMode);
            Assert.True(shell.IsBotMode);
            Assert.True(shell.Rail.IsBotMode);
            Assert.All(shell.Rail.Chips, c => Assert.False(c.IsCurrent));

            // Selecting a project on the rail returns to project mode.
            shell.Rail.Chips.Single(c => c.Id == project.Id).SelectCommand.Execute(null);
            Assert.False(bots.IsBotMode);
            Assert.False(shell.IsBotMode);
            Assert.True(shell.Rail.Chips.Single(c => c.Id == project.Id).IsCurrent);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task BotPanel_SchemeATree_CreatesBotExpandsChatsAndFilters()
    {
        var bots = AppHost.Get<IBotService>();
        var dialogs = AppHost.Get<FakeDialogService>();
        var panel = AppHost.Get<BotPanelViewModel>();

        Assert.True(panel.IsEmpty);
        Assert.NotEmpty(panel.StarterTemplates);

        dialogs.NextBotRequest = new NewBotRequest("PR 审查员", "claude", "#3b82d6", "pr", "自动审查 PR");
        await panel.NewBotCommand.ExecuteAsync(null);

        Assert.False(panel.IsEmpty);
        Assert.Equal(1, panel.BotCount);
        Assert.Single(panel.BotNodes);
        var node = panel.BotNodes[0];
        Assert.Equal("PR 审查员", node.Name);
        Assert.True(node.IsExpanded);
        Assert.Single(node.Chats);
        Assert.True(node.Chats[0].IsHome);

        // Add a second chat from the tree node.
        node.NewChatCommand.Execute(null);
        Assert.Equal(2, panel.BotNodes[0].Chats.Count);

        // Toggle collapse on active bot.
        panel.BotNodes[0].ToggleCommand.Execute(null);
        Assert.False(panel.BotNodes[0].IsExpanded);

        // Search expands matching bots and filters chats.
        panel.SearchQuery = "Home";
        Assert.Single(panel.BotNodes);
        Assert.True(panel.BotNodes[0].IsExpanded);
        Assert.Single(panel.BotNodes[0].Chats);
    }

    [AvaloniaFact]
    public void BotRightPanel_AndConnectorCatalog_WorkAcrossAllFourTabs()
    {
        var bots = AppHost.Get<IBotService>();
        var right = AppHost.Get<BotRightPanelViewModel>();

        var bot = bots.CreateBot(new NewBotRequest("西瓜", "claude", "#2f9e63", "blank", "负责代码巡检"));
        File.WriteAllText(bots.Home.GetMemoryPath(bot), "# Memory\n- Rule: Never merge without approval");
        bots.AddWorkItem(bot.Id, "PR #131 CI green", BotWorkItemStatus.NeedsYou, "Merge now?", ["Merge", "Hold"], bot.HomeChatId);

        right.Refresh();

        // Tab 1: Home
        Assert.True(right.IsHomeTab);
        Assert.Equal("西瓜", right.BotName);
        Assert.Equal("bot", right.BotSlug);
        Assert.True(right.HasWorkItems);
        Assert.True(right.HasMemory);
        Assert.Equal(1, right.NeedsCount);

        // Tab 2: Connectors + Catalog modal VM
        right.TabKey = "connectors";
        Assert.True(right.IsConnectorsTab);
        Assert.True(right.HasNoConnectors);
        Assert.Contains("西瓜", right.ConnectorsEmptyDescription);

        var catalog = new ConnectorCatalogViewModel(bot, bots);
        Assert.NotEmpty(catalog.Categories);
        Assert.NotEmpty(catalog.Groups);

        // Filter by coding category (8 global + 3 domestic apps: Gitee, Yunxiao, JihuLab)
        var coding = catalog.Categories.Single(c => c.Id == "coding");
        Assert.Equal(11, coding.Count);
        coding.SelectCommand.Execute(null);
        Assert.Single(catalog.Groups);
        Assert.Equal(11, catalog.Groups[0].Apps.Count);

        // Add GitHub from catalog and add custom URL connector
        catalog.Groups[0].Apps.Single(a => a.AppId == "github").ToggleCommand.Execute(null);
        catalog.CustomUrlName = "Local MCP";
        catalog.CustomUrl = "https://mcp.example.com/sse";
        catalog.AddByUrlCommand.Execute(null);

        Assert.True(right.HasConnectors);
        Assert.Equal(2, right.Connectors.Count);

        // Search domestic apps by Chinese name (Feishu, AMap, Tencent Docs)
        catalog.Categories.Single(c => c.Id == "all").SelectCommand.Execute(null);
        catalog.SearchQuery = "飞书";
        Assert.Contains(catalog.Groups.SelectMany(g => g.Apps), a => a.AppId == "feishu");
        catalog.SearchQuery = "高德";
        Assert.Contains(catalog.Groups.SelectMany(g => g.Apps), a => a.AppId == "amap");
        catalog.SearchQuery = "";

        // Tab 3: Chats (search + close + reopen)
        right.TabKey = "chats";
        Assert.True(right.IsChatsTab);
        var extraChat = bots.CreateChat(bot.Id, "codex", "Codex Chat")!;
        Assert.Equal(2, right.FilteredChats.Count);
        bots.CloseChat(bot.Id, extraChat.Id);
        Assert.True(right.FilteredChats.Single(c => c.ChatId == extraChat.Id).IsClosed);

        // Tab 4: Files (points at bot's home directory)
        right.TabKey = "files";
        Assert.True(right.IsFilesTab);
        Assert.Equal(bots.GetHomePath(bot), right.Files.Root);
    }

    [AvaloniaFact]
    public void BotMode_WorksIndependentlyWithZeroProjects_AndRendersDialogsCleanly()
    {
        var projects = AppHost.Get<IProjectService>();
        var bots = AppHost.Get<IBotService>();
        var shellVm = AppHost.Get<ShellViewModel>();

        Assert.Empty(projects.Projects);
        bots.SetBotMode(true);
        Assert.True(shellVm.Workspace.IsBotMode);
        Assert.True(shellVm.Workspace.ShowNoBotEmpty);
        Assert.False(shellVm.Workspace.ShowNoProjectEmpty);

        // Render NewBotDialogView inside a window to ensure Arc inputs & selects bind cleanly.
        var catalog = AppHost.Get<Octopus.Agents.IAgentCatalog>();
        var scanner = AppHost.Get<Octopus.Agents.IAgentScanner>();
        var settings = AppHost.Get<Octopus.Storage.ISettingsStore>();
        var newBotVm = new NewBotDialogViewModel(catalog, scanner, settings);
        var dlgWindow = new Window { Width = 520, Height = 560, Content = new NewBotDialogView(newBotVm) };
        dlgWindow.Show();
        try
        {
            Assert.NotEmpty(newBotVm.HostOptions);
            Assert.NotEmpty(newBotVm.AgentOptions);
            Assert.Equal("local", newBotVm.SelectedHostKey);
        }
        finally
        {
            dlgWindow.Close();
        }

        // Creating a bot with 0 projects succeeds and opens its dock layout cleanly.
        var bot = bots.CreateBot(new NewBotRequest("西瓜", "claude", "#2f9e63", "blank", "独立运行", "local"));
        Assert.Equal("local", bot.HostDeviceKey);
        Assert.Empty(projects.Projects);
        Assert.True(shellVm.Workspace.HasProject);
        Assert.NotNull(shellVm.Workspace.CurrentLayout);
    }

    [AvaloniaFact]
    public void BotHomeSettings_ShareSecurityCheck_AndAvatarPicker_WorkCleanly()
    {
        var bots = AppHost.Get<IBotService>();
        var right = AppHost.Get<BotRightPanelViewModel>();
        var bot = bots.CreateBot(new NewBotRequest("西瓜", "claude", "#93c5fd", "blank", "负责巡检", "local"));

        right.Refresh();
        Assert.True(right.IsHomeOverview);
        Assert.False(right.IsHomeSettings);

        // Enter in-panel settings (< 西瓜 / 设置)
        right.OpenSettingsCommand.Execute(null);
        Assert.True(right.IsHomeSettings);
        Assert.False(right.IsHomeOverview);

        // Toggle settings & verify persistence on BotDefinition
        right.LookAroundEnabled = false;
        right.TidyUpMemoryEnabled = false;
        right.StaleAfterDaysKey = "7";
        right.NotifyTaskCompleted = false;
        right.CyclePermissionModeCommand.Execute(null);
        right.CycleWhoCanAskCommand.Execute(null);

        var updated = bots.Bots.Single(b => b.Id == bot.Id);
        Assert.False(updated.LookAroundEnabled);
        Assert.False(updated.TidyUpMemoryEnabled);
        Assert.Equal(7, updated.StaleAfterDays);
        Assert.False(updated.NotifyTaskCompleted);
        Assert.Equal("ask", updated.PermissionMode);
        Assert.Equal("members", updated.WhoCanAsk);

        // Share dialog: security check clean vs secret detected
        var shareVm = new BotShareDialogViewModel(updated, bots);
        Assert.True(shareVm.SecurityCheckClean);
        File.WriteAllText(bots.Home.GetMemoryPath(updated), "# Memory\n- token sk-secret123456");
        shareVm.IncludeMemory = true;
        Assert.False(shareVm.SecurityCheckClean);
        Assert.Contains("SOUL.md", shareVm.BuildShareBundle());

        var shareWin = new Window { Width = 480, Height = 560, Content = new BotShareDialogView(shareVm) };
        shareWin.Show();
        shareWin.Close();

        // Avatar picker: carousel cycling & rendering
        var avatarVm = new BotAvatarPickerViewModel(updated);
        avatarVm.NextCommand.Execute(null);
        Assert.Equal("Bot", avatarVm.Choice.Icon);
        var avatarWin = new Window { Width = 460, Height = 360, Content = new BotAvatarPickerView(avatarVm) };
        avatarWin.Show();
        avatarWin.Close();

        // Return to Home overview
        right.CloseSettingsCommand.Execute(null);
        Assert.True(right.IsHomeOverview);
    }

    [AvaloniaFact]
    public void ShellView_RendersBotPanelsWithoutErrors()
    {
        var bots = AppHost.Get<IBotService>();
        bots.CreateBot(new NewBotRequest("西瓜", "claude", "#2f9e63", "blank", "日常协助"));

        var shellVm = AppHost.Get<ShellViewModel>();
        shellVm.IsRightPanelOpen = true;
        var window = new Window { Width = 1280, Height = 800, Content = new ShellView { DataContext = shellVm } };
        window.Show();
        try
        {
            Assert.True(shellVm.IsBotMode);
            Assert.True(shellVm.Workspace.HasProject);
            Assert.NotNull(shellVm.Workspace.CurrentLayout);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task BotConnectors_ResolveVendorMcpUrls_SyncMcpJson_AndInjectSecretsIntoAcp()
    {
        var bots = AppHost.Get<IBotService>();
        var secrets = AppHost.Get<ISecretStore>();

        // Verify vendor MCP URL template expansion
        var gitlab = BotCatalog.FindConnectorApp("gitlab")!;
        Assert.Equal("https://gitlab.com/api/v4/mcp", BotCatalog.ResolveMcpUrl(gitlab));
        Assert.Equal("https://git.corp.internal/api/v4/mcp", BotCatalog.ResolveMcpUrl(gitlab, "https://git.corp.internal/"));

        var datadog = BotCatalog.FindConnectorApp("datadog")!;
        Assert.Equal("https://mcp.datadoghq.eu/v1/mcp", BotCatalog.ResolveMcpUrl(datadog, "datadoghq.eu"));

        var bot = bots.CreateBot(new NewBotRequest("MCP Bot", "claude", "#5b5bd6", "blank", "MCP 集成测试"));

        // Add Context7 (no-auth official remote MCP) + custom SSE MCP with Bearer token
        var c7 = bots.AddConnector(bot.Id, "context7")!;
        Assert.Equal("https://mcp.context7.com/mcp", c7.Url);
        Assert.Equal("none", c7.AuthMode);

        var custom = (await bots.AddCustomUrlConnectorAsync(
            bot.Id,
            "Internal MCP",
            "https://mcp.internal.test/sse",
            transport: "sse",
            bearerToken: "secret-pat-999"))!;
        Assert.True(custom.HasSecret);
        Assert.Equal("sse", custom.Transport);

        // .mcp.json is written with both servers, NEVER containing the plaintext secret
        var mcpJsonPath = bots.Home.GetMcpConfigPath(bot);
        Assert.True(File.Exists(mcpJsonPath));
        var mcpJson = File.ReadAllText(mcpJsonPath);
        Assert.Contains("https://mcp.context7.com/mcp", mcpJson);
        Assert.Contains("https://mcp.internal.test/sse", mcpJson);
        Assert.DoesNotContain("secret-pat-999", mcpJson);

        // ACP MCP server list includes Authorization header resolved from ISecretStore
        var acpServers = await bots.BuildAcpMcpServersAsync(bot.Id);
        Assert.Equal(2, acpServers.Count);
        var sseServer = Assert.Single(acpServers, s => s.Transport == "sse");
        Assert.Equal("Bearer secret-pat-999", sseServer.Headers["Authorization"]);

        // Merge with global ACP MCP servers gives precedence to Bot-specific connectors
        var merged = BotConnectorRuntime.MergeMcpServers(
            [
                new AcpMcpServerConfig { Name = "context7", Transport = "http", Url = "https://old.example/mcp" },
                new AcpMcpServerConfig { Name = "global-only", Transport = "http", Url = "https://global.example/mcp" },
            ],
            acpServers);
        Assert.Equal(3, merged.Count);
        Assert.Equal("https://mcp.context7.com/mcp", merged.Single(s => s.Name == "context7").Url);
        Assert.Contains(merged, s => s.Name == "global-only");

        // Removing the custom connector deletes its secret from ISecretStore
        bots.RemoveConnector(bot.Id, custom.Id);
        await Task.Delay(50);
        Assert.Null(await secrets.GetAsync(BotConnectorRuntime.SecretService, BotConnectorRuntime.SecretName(bot.Id, custom.Id)));

        // Disabling remaining connector removes stale .mcp.json
        bots.SetConnectorEnabled(bot.Id, c7.Id, false);
        Assert.False(File.Exists(mcpJsonPath));
    }

    [AvaloniaFact]
    public async Task BotShareLink_RoundTripsAndImportsViaDeepLinkAndPanelCommand()
    {
        var bots = AppHost.Get<IBotService>();
        var dialogs = AppHost.Get<FakeDialogService>();
        var panel = AppHost.Get<BotPanelViewModel>();

        var original = bots.CreateBot(new NewBotRequest("巡检官", "claude", "#3b82d6", "ci", "定时巡检构建与日志"));
        await bots.AddCustomUrlConnectorAsync(original.Id, "Corp MCP", "https://mcp.corp.test/mcp", "http", "super-secret-token");
        bots.UpdateBotSettings(original.Id, b =>
        {
            b.SetupNotes = "请先配置 Corp MCP 的 Bearer Token";
            b.LookAroundIntervalHours = 2;
        });

        var shareVm = new BotShareDialogViewModel(bots.Bots.Single(b => b.Id == original.Id), bots);
        var link = shareVm.BuildShareLink();
        Assert.StartsWith(BotShareCodec.ImportUriPrefix, link, StringComparison.Ordinal);

        // Decode & validate no secrets were included
        Assert.True(BotShareCodec.TryDecodeString(link, out var decoded, out var error));
        Assert.Null(error);
        Assert.NotNull(decoded);
        Assert.Equal("巡检官", decoded.Name);
        Assert.Equal(2, decoded.LookAroundIntervalHours);
        Assert.Single(decoded.Connectors);
        Assert.DoesNotContain("super-secret-token", link);

        // Invalid links are rejected safely
        Assert.False(BotShareCodec.TryDecodeString("octopus://bot/import?data=!!!invalid!!!", out _, out _));
        Assert.False(BotShareCodec.TryDecodeString("https://example.com/bot", out _, out _));

        // Render BotImportDialogView headlessly
        var importVm = new BotImportDialogViewModel(decoded);
        Assert.True(importVm.SecurityCheckClean);
        Assert.True(importVm.HasSetupNotes);
        var win = new Window { Width = 460, Height = 520, Content = new BotImportDialogView(importVm) };
        win.Show();
        win.Close();

        // Import via BotPanelViewModel.ImportBotFromLinkCommand
        dialogs.NameAnswer = link;
        dialogs.ConfirmBotImportAnswer = true;
        await panel.ImportBotFromLinkCommand.ExecuteAsync(null);

        Assert.Equal(2, bots.Bots.Count);
        var imported = bots.CurrentBot!;
        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal("巡检官", imported.Name);
        Assert.Single(imported.Connectors);
        Assert.False(imported.Connectors[0].HasSecret);
        Assert.Contains("定时巡检构建与日志", File.ReadAllText(bots.Home.GetSoulPath(imported)));
        Assert.True(File.Exists(bots.Home.GetMcpConfigPath(imported)));
    }

    [AvaloniaFact, FreshApp]
    public void BotRailAvatarFrame_ShowsEmptySingleGridAndCapsAtFour()
    {
        var bots = AppHost.Get<IBotService>();
        var rail = AppHost.Get<ProjectRailViewModel>();

        var win = new Window { Width = 120, Height = 500, Content = new ProjectRailView { DataContext = rail } };
        win.Show();
        try
        {
            // 0 bots, inactive: dashed empty avatar frame.
            Assert.True(rail.ShowEmptyBotFrame);
            Assert.True(rail.ShowEmptyBotDashedFrame);
            Assert.False(rail.ShowBotIdleFrame);
            Assert.False(rail.HasAnyBot);
            Assert.False(rail.ShowSingleBotAvatar);
            Assert.False(rail.ShowBotAvatarGrid);
            Assert.Null(rail.SingleBotAvatar);
            Assert.Empty(rail.BotAvatars);

            // Entering Bot mode with 0 bots swaps dashed frame to accent highlight ring.
            rail.EnterBotModeCommand.Execute(null);
            Assert.True(rail.IsBotMode);
            Assert.False(rail.ShowEmptyBotDashedFrame);
            Assert.False(rail.ShowBotIdleFrame);

            // 1 bot: fills the frame with that bot's full avatar.
            var b1 = bots.CreateBot(new NewBotRequest("Alpha", "claude", "#2f9e63", "blank", null));
            Assert.True(rail.HasAnyBot);
            Assert.True(rail.ShowSingleBotAvatar);
            Assert.False(rail.ShowBotAvatarGrid);
            Assert.NotNull(rail.SingleBotAvatar);
            Assert.Equal("A", rail.SingleBotAvatar!.Initial);
            Assert.True(rail.SingleBotAvatar.ShowInitial);
            Assert.Equal(Color.Parse("#2f9e63"), Assert.IsAssignableFrom<ISolidColorBrush>(rail.SingleBotAvatar.Background).Color);

            // Updating appearance refreshes the rail avatar immediately.
            bots.SetBotAppearance(b1.Id, "#3b82d6", "Cat", null);
            Assert.NotNull(rail.SingleBotAvatar);
            Assert.True(rail.SingleBotAvatar!.HasIcon);
            Assert.Equal(LucideIconKind.Cat, rail.SingleBotAvatar.EffectiveIconKind);
            Assert.Equal(Color.Parse("#3b82d6"), Assert.IsAssignableFrom<ISolidColorBrush>(rail.SingleBotAvatar.Background).Color);

            // Leaving Bot mode shows the idle avatar frame border.
            bots.SetBotMode(false);
            Assert.True(rail.ShowBotIdleFrame);
            Assert.False(rail.ShowEmptyBotDashedFrame);

            // 2..4 bots: switches to 2x2 grid.
            var b2 = bots.CreateBot(new NewBotRequest("Beta", "claude", "#5b5bd6", "blank", null));
            Assert.False(rail.ShowSingleBotAvatar);
            Assert.True(rail.ShowBotAvatarGrid);
            Assert.Null(rail.SingleBotAvatar);
            Assert.Equal(2, rail.BotAvatars.Count);

            var b3 = bots.CreateBot(new NewBotRequest("Gamma", "claude", "#d97757", "blank", null));
            var b4 = bots.CreateBot(new NewBotRequest("Delta", "claude", "#64748b", "blank", null));
            Assert.Equal(4, rail.BotAvatars.Count);

            // 5th bot: overflow is omitted, rail keeps only the first 4 avatars.
            var b5 = bots.CreateBot(new NewBotRequest("Epsilon", "claude", "#1e293b", "blank", null));
            Assert.Equal(5, bots.Bots.Count);
            Assert.Equal(ProjectRailViewModel.MaxBotRailAvatars, rail.BotAvatars.Count);
            Assert.Equal(new[] { b5.Id, b4.Id, b3.Id, b2.Id }, rail.BotAvatars.Select(t => t.Id));

            // Deleting back to 1 restores single avatar; deleting last restores empty dashed frame.
            bots.DeleteBot(b5.Id);
            bots.DeleteBot(b4.Id);
            bots.DeleteBot(b3.Id);
            bots.DeleteBot(b2.Id);
            Assert.True(rail.ShowSingleBotAvatar);
            Assert.Equal(b1.Id, rail.SingleBotAvatar?.Id);

            bots.DeleteBot(b1.Id);
            bots.SetBotMode(false);
            Assert.True(rail.ShowEmptyBotFrame);
            Assert.True(rail.ShowEmptyBotDashedFrame);
            Assert.Empty(rail.BotAvatars);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact, FreshApp]
    public void NewBotDialog_LocalHostShowsOnlyInstalledEnabledAgents()
    {
        var catalog = AppHost.Get<Octopus.Agents.IAgentCatalog>();
        var settings = AppHost.Get<Octopus.Storage.ISettingsStore>();
        var scanner = new FakeAgentScanner("claude", "codex", "opencode");

        // Disable opencode and set codex as default.
        settings.Update(s =>
        {
            s.Agents.DefaultAgentId = "codex";
            s.Agents.Profiles["opencode"] = new AgentProfile { Enabled = false };
        });

        var hostOptions = new List<TKCode.Arc.SelectOption>
        {
            new("local", "Local"),
            new("ssh:devbox", "devbox"),
        };

        var vm = new NewBotDialogViewModel(catalog, scanner, settings, hostOptions: hostOptions, initialHostKey: "local");
        Assert.True(vm.HasAgents);
        Assert.Equal(["claude", "codex"], vm.AgentOptions.Select(o => o.Value?.ToString() ?? "").ToArray());
        Assert.Equal("codex", vm.SelectedAgentId);

        // Switching to an SSH host includes enabled catalog agents beyond local installations.
        vm.SelectedHostKey = "ssh:devbox";
        Assert.True(vm.AgentOptions.Count > 2);
        Assert.Equal("codex", vm.SelectedAgentId);

        // Switching back to local restores strictly installed & enabled agents.
        vm.SelectedHostKey = "local";
        Assert.Equal(["claude", "codex"], vm.AgentOptions.Select(o => o.Value?.ToString() ?? "").ToArray());

        // When no agents are installed locally, AgentOptions is empty (never falls back to uninstalled agents).
        var emptyScanner = new FakeAgentScanner();
        var emptyVm = new NewBotDialogViewModel(catalog, emptyScanner, settings);
        Assert.False(emptyVm.HasAgents);
        Assert.Empty(emptyVm.AgentOptions);
        Assert.Equal("", emptyVm.SelectedAgentId);
    }

    [AvaloniaFact, FreshApp]
    public void BotRightPanel_CycleAgent_CyclesOnlyInstalledEnabledAgents()
    {
        var bots = AppHost.Get<IBotService>();
        var settings = AppHost.Get<Octopus.Storage.ISettingsStore>();
        var right = AppHost.Get<BotRightPanelViewModel>();

        // TestApp registers FakeAgentScanner("claude", "opencode", "codex").
        settings.Update(s => s.Agents.Profiles["opencode"] = new AgentProfile { Enabled = false });

        var bot = bots.CreateBot(new NewBotRequest("西瓜", "claude", "#2f9e63", "blank", "巡检"));
        right.Refresh();

        right.CycleAgentCommand.Execute(null);
        Assert.Equal("codex", bots.Bots.Single(b => b.Id == bot.Id).AgentId);

        right.CycleAgentCommand.Execute(null);
        Assert.Equal("claude", bots.Bots.Single(b => b.Id == bot.Id).AgentId);

        // Dropdown options contain only installed & enabled agents and update BotDefinition on selection.
        Assert.Equal(new[] { "claude", "codex" }, right.AgentOptions.Select(o => o.Value).ToArray());
        right.SelectedAgentId = "codex";
        Assert.Equal("codex", bots.Bots.Single(b => b.Id == bot.Id).AgentId);

        right.SelectedPermissionMode = "read_only";
        Assert.Equal("read_only", bots.Bots.Single(b => b.Id == bot.Id).PermissionMode);

        right.SelectedWhoCanAsk = "anyone";
        Assert.Equal("anyone", bots.Bots.Single(b => b.Id == bot.Id).WhoCanAsk);
    }

    [AvaloniaFact]
    public void ConnectorIcons_LoadRealLogosForAllBuiltInApps_AndRenderCatalogCleanly()
    {
        foreach (var app in BotCatalog.ConnectorApps)
        {
            Assert.True(ConnectorIconStore.Instance.Contains(app.Id), $"Missing logo for connector '{app.Id}'");
            var graphic = ConnectorIconStore.Instance.Get(app.Id);
            Assert.NotNull(graphic);
            Assert.NotEmpty(graphic!.Shapes);
        }

        // Unknown / custom connector ids fall back cleanly to null (badge rendering).
        Assert.Null(ConnectorIconStore.Instance.Get("custom-mcp-url"));

        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("Logo Bot", "claude", "#3b82d6", "pr", null));
        var catalogVm = new ConnectorCatalogViewModel(bot, bots);
        Assert.Equal(BotCatalog.ConnectorApps.Count + catalogVm.Groups.Count, catalogVm.CatalogItems.Count);
        var catalogView = new ConnectorCatalogView(catalogVm);
        var window = new Window { Width = 680, Height = 520, Content = catalogView };
        window.Show();
        try
        {
            window.UpdateLayout();
            var catalogList = Assert.IsType<ItemsControl>(catalogView.FindControl<ItemsControl>("CatalogList"));
            Assert.IsType<VirtualizingStackPanel>(catalogList.ItemsPanelRoot);
            // Genuine virtualization: realized containers in a 460px viewport must be strictly fewer than all 33 catalog items.
            var realized = catalogList.GetRealizedContainers().Count();
            Assert.True(realized > 0 && realized < catalogVm.CatalogItems.Count,
                $"Expected virtualized subset of containers, got {realized}/{catalogVm.CatalogItems.Count}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact, FreshApp]
    public void BotAndChatContextMenus_SupportSettingsCopyPathUnreadOpenAndDelete()
    {
        var bots = AppHost.Get<IBotService>();
        var panel = AppHost.Get<BotPanelViewModel>();
        var shell = AppHost.Get<ShellViewModel>();
        var right = AppHost.Get<BotRightPanelViewModel>();

        var bot = bots.CreateBot(new NewBotRequest("西瓜", "claude", "#2f9e63", "blank", null));
        var extraChat = bots.CreateChat(bot.Id, "claude", "Claude Chat")!;

        Assert.Single(panel.BotNodes);
        var node = panel.BotNodes[0];

        // Bot context menu has all 6 expected entries matching the reference design.
        var botButton = new Avalonia.Controls.Button();
        BotContextMenus.AttachBotMenu(botButton, node);
        var botMenu = Assert.IsType<MenuSurface>(TKCode.Arc.ContextMenu.GetMenu(botButton));
        Assert.Equal(6, botMenu.Items.Count);
        Assert.True(Assert.IsType<MenuEntry>(botMenu.Items[5]).IsDestructive);

        // Chat context menu: regular chat has Open + destructive Delete chat; Home chat has Open only.
        var extraRow = node.Chats.Single(c => c.ChatId == extraChat.Id);
        var chatButton = new Avalonia.Controls.Button();
        BotContextMenus.AttachChatMenu(chatButton, extraRow);
        var chatMenu = Assert.IsType<MenuSurface>(TKCode.Arc.ContextMenu.GetMenu(chatButton));
        Assert.Equal(2, chatMenu.Items.Count);
        Assert.True(Assert.IsType<MenuEntry>(chatMenu.Items[1]).IsDestructive);

        var homeRow = node.Chats.Single(c => c.IsHome);
        var homeButton = new Avalonia.Controls.Button();
        BotContextMenus.AttachChatMenu(homeButton, homeRow);
        var homeMenu = Assert.IsType<MenuSurface>(TKCode.Arc.ContextMenu.GetMenu(homeButton));
        Assert.Single(homeMenu.Items);

        // Copy path emits the Bot's home folder path.
        string? copied = null;
        panel.CopyRequested += (_, path) => copied = path;
        node.CopyPathCommand.Execute(null);
        Assert.Equal(bots.GetHomePath(bot), copied);

        // Mark as unread toggles IsUnread, shows unread dot, increments attention count, and clears on OpenChat.
        node.ToggleUnreadCommand.Execute(null);
        Assert.True(panel.BotNodes[0].IsUnread);
        Assert.True(panel.BotNodes[0].ShowUnreadDot);
        Assert.Equal(1, bots.TotalNeedsAttentionCount());

        bots.OpenChat(bot.Id, extraChat.Id);
        Assert.False(panel.BotNodes[0].IsUnread);
        Assert.Equal(0, bots.TotalNeedsAttentionCount());

        // "Bot Settings..." opens the right panel in Bot Settings view.
        shell.IsRightPanelOpen = false;
        panel.BotNodes[0].OpenSettingsCommand.Execute(null);
        Assert.True(shell.IsRightPanelOpen);
        Assert.True(right.IsHomeSettings);

        // Deleting a non-home chat via right-click command removes it.
        extraRow.DeleteChatCommand.Execute(null);
        Assert.Single(panel.BotNodes[0].Chats);
        Assert.True(panel.BotNodes[0].Chats[0].IsHome);
    }

    [AvaloniaFact]
    public async Task BotConnectorOAuth_DiscoversRegistersExchangesAndRefreshesTokens()
    {
        var secrets = new Octopus.Platform.MemorySecretStore();
        var issuedCode = "auth-code-linear-42";
        string? registeredRedirect = null;
        string? expectedChallenge = null;
        string? lastGrantType = null;

        var handler = new StubHttpHandler(async (req, ct) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url == "https://mcp.demo.test/mcp")
            {
                var resp = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
                resp.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    """Bearer realm="OAuth", resource_metadata="https://mcp.demo.test/.well-known/oauth-protected-resource/mcp", scope="read write" """);
                return resp;
            }

            if (url == "https://mcp.demo.test/.well-known/oauth-protected-resource/mcp")
            {
                return JsonResponse("""{"resource":"https://mcp.demo.test/mcp","authorization_servers":["https://auth.demo.test"],"scopes_supported":["read","write"]}""");
            }

            if (url == "https://auth.demo.test/.well-known/oauth-authorization-server")
            {
                return JsonResponse("""{"issuer":"https://auth.demo.test","authorization_endpoint":"https://auth.demo.test/authorize","token_endpoint":"https://auth.demo.test/token","registration_endpoint":"https://auth.demo.test/register","token_endpoint_auth_methods_supported":["none"],"code_challenge_methods_supported":["S256"]}""");
            }

            if (url == "https://auth.demo.test/register" && req.Method == HttpMethod.Post)
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                registeredRedirect = doc.RootElement.GetProperty("redirect_uris")[0].GetString();
                return JsonResponse("""{"client_id":"dyn-client-123","token_endpoint_auth_method":"none"}""", System.Net.HttpStatusCode.Created);
            }

            if (url == "https://auth.demo.test/token" && req.Method == HttpMethod.Post)
            {
                var form = BotMcpOAuthClient.ParseQueryString(await req.Content!.ReadAsStringAsync(ct));
                lastGrantType = form["grant_type"];
                if (lastGrantType == "authorization_code")
                {
                    Assert.Equal(issuedCode, form["code"]);
                    Assert.Equal("dyn-client-123", form["client_id"]);
                    Assert.Equal(registeredRedirect, form["redirect_uri"]);
                    Assert.Equal(expectedChallenge, BotMcpOAuthClient.ComputeS256Challenge(form["code_verifier"]));
                    Assert.Equal("https://mcp.demo.test/mcp", form["resource"]);
                    return JsonResponse("""{"access_token":"oauth-access-v1","refresh_token":"oauth-refresh-v1","expires_in":3600}""");
                }
                if (lastGrantType == "refresh_token")
                {
                    Assert.Equal("oauth-refresh-v1", form["refresh_token"]);
                    Assert.Equal("dyn-client-123", form["client_id"]);
                    return JsonResponse("""{"access_token":"oauth-access-v2","refresh_token":"oauth-refresh-v2","expires_in":3600}""");
                }
            }

            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        using var callbackClient = new HttpClient();
        var opener = new CallbackDrivingOpener(authUrl =>
        {
            var qIdx = authUrl.IndexOf('?');
            var q = BotMcpOAuthClient.ParseQueryString(authUrl[(qIdx + 1)..]);
            expectedChallenge = q["code_challenge"];
            Assert.Equal("S256", q["code_challenge_method"]);
            Assert.Equal("read write", q["scope"]);
            var redirectUri = q["redirect_uri"];
            var state = q["state"];
            _ = Task.Run(async () =>
            {
                var callbackUrl = $"{redirectUri}?code={Uri.EscapeDataString(issuedCode)}&state={Uri.EscapeDataString(state)}";
                using var cbResp = await callbackClient.GetAsync(callbackUrl);
                Assert.True(cbResp.IsSuccessStatusCode);
            });
            return true;
        });

        var runtime = new BotConnectorRuntime(secrets, opener, http);
        var signIn = await runtime.SignInOAuthAsync("bot-1", "conn-1", "https://mcp.demo.test/mcp");
        Assert.True(signIn.Succeeded, signIn.Error);
        Assert.NotNull(signIn.Credential);
        Assert.Equal("oauth-access-v1", signIn.Credential!.AccessToken);
        Assert.Equal("authorization_code", lastGrantType);

        // Runtime returns the active access token and builds ACP Authorization header cleanly.
        Assert.Equal("oauth-access-v1", await runtime.GetTokenAsync("bot-1", "conn-1"));
        var bot = new BotDefinition
        {
            Id = "bot-1",
            Connectors =
            [
                new BotConnectorBinding
                {
                    Id = "conn-1",
                    AppId = "linear",
                    DisplayName = "Linear",
                    Url = "https://mcp.demo.test/mcp",
                    Transport = "http",
                    AuthMode = "oauth",
                    HasSecret = true,
                    Enabled = true,
                }
            ]
        };
        var servers = await runtime.BuildAcpMcpServersAsync(bot);
        Assert.Equal("Bearer oauth-access-v1", Assert.Single(servers).Headers["Authorization"]);

        // Simulate an expiring credential and verify transparent refresh_token rotation.
        var expired = signIn.Credential with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-30) };
        await runtime.SetOAuthCredentialAsync("bot-1", "conn-1", expired);

        var refreshedToken = await runtime.GetTokenAsync("bot-1", "conn-1");
        Assert.Equal("oauth-access-v2", refreshedToken);
        Assert.Equal("refresh_token", lastGrantType);

        // Refreshed credential is persisted back to ISecretStore.
        var storedRaw = await secrets.GetAsync(BotConnectorRuntime.SecretService, BotConnectorRuntime.SecretName("bot-1", "conn-1"));
        Assert.True(BotOAuthCredential.TryDeserialize(storedRaw, out var persisted));
        Assert.Equal("oauth-access-v2", persisted!.AccessToken);
        Assert.Equal("oauth-refresh-v2", persisted.RefreshToken);
    }

    [AvaloniaFact]
    public void BotRightPanel_ConnectorsCard_RendersOAuthAndExpandedEditorWithoutOverflow()
    {
        var bots = AppHost.Get<IBotService>();
        var right = AppHost.Get<BotRightPanelViewModel>();
        var bot = bots.CreateBot(new NewBotRequest("OAuth Bot", "claude", "#5b5bd6", "blank", null));
        bots.AddConnector(bot.Id, "linear");
        bots.AddConnector(bot.Id, "datadog");

        right.TabKey = "connectors";
        right.Refresh();

        Assert.Equal(2, right.Connectors.Count);
        var linear = right.Connectors.Single(c => c.AppId == "linear");
        Assert.True(linear.IsOAuthPreferred);
        Assert.True(linear.ShowQuickOAuthBar);
        Assert.True(linear.CanUseOAuth);

        linear.ToggleEditCommand.Execute(null);
        Assert.True(linear.IsEditing);
        Assert.False(linear.ShowQuickOAuthBar);

        var view = new BotRightPanelView { DataContext = right };
        var win = new Window { Width = 310, Height = 640, Content = view };
        win.Show();
        try
        {
            win.UpdateLayout();
            Assert.True(view.Bounds.Width <= 310.5);
            var connectorsList = Assert.IsType<ItemsControl>(view.FindControl<ItemsControl>("ConnectorsList"));
            Assert.IsType<VirtualizingStackPanel>(connectorsList.ItemsPanelRoot);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task DomesticConnectors_SupportFeishuPostOAuthDiscovery_AndApiKeyTemplateResolution()
    {
        // 1. Verify domestic catalog definitions & URL variable behavior
        var feishu = BotCatalog.FindConnectorApp("feishu")!;
        Assert.Equal("https://mcp.feishu.cn/mcp", BotCatalog.ResolveMcpUrl(feishu));
        Assert.Equal("oauth", feishu.AuthMode);
        Assert.Equal("飞书", feishu.DisplayName(isZh: true));
        Assert.Equal("Feishu", feishu.DisplayName(isZh: false));

        var tencentDocs = BotCatalog.FindConnectorApp("tencentdocs")!;
        Assert.Equal("https://docs.qq.com/openapi/mcp", BotCatalog.ResolveMcpUrl(tencentDocs));
        Assert.Equal("oauth", tencentDocs.AuthMode);

        var gitee = BotCatalog.FindConnectorApp("gitee")!;
        Assert.Equal("https://api.gitee.com/mcp", BotCatalog.ResolveMcpUrl(gitee));
        Assert.Equal("bearer", gitee.AuthMode);

        var yunxiao = BotCatalog.FindConnectorApp("yunxiao")!;
        Assert.Equal("https://openapi-rdc.aliyuncs.com/ai/mcp", BotCatalog.ResolveMcpUrl(yunxiao));

        var jihulab = BotCatalog.FindConnectorApp("jihulab")!;
        Assert.Equal("https://jihulab.com/api/v4/mcp", BotCatalog.ResolveMcpUrl(jihulab));
        Assert.Equal("oauth", jihulab.AuthMode);

        // Map connectors require an API key before resolving to a valid URL (never write broken "?key=" to .mcp.json)
        var amap = BotCatalog.FindConnectorApp("amap")!;
        Assert.Null(BotCatalog.ResolveMcpUrl(amap));
        Assert.Equal("https://mcp.amap.com/mcp?key=amap-key-123", BotCatalog.ResolveMcpUrl(amap, "amap-key-123"));

        var baidu = BotCatalog.FindConnectorApp("baidumap")!;
        Assert.Null(BotCatalog.ResolveMcpUrl(baidu));
        Assert.Equal("https://mcp.map.baidu.com/mcp?ak=bd-ak-456", BotCatalog.ResolveMcpUrl(baidu, "bd-ak-456"));

        var tencentMap = BotCatalog.FindConnectorApp("tencentmap")!;
        Assert.Null(BotCatalog.ResolveMcpUrl(tencentMap));
        Assert.Equal("https://mcp.map.qq.com/mcp?key=qq-key-789&format=0", BotCatalog.ResolveMcpUrl(tencentMap, "qq-key-789"));

        // 2. Verify Feishu-style POST-only 401 WWW-Authenticate discovery + cross-origin path issuer metadata
        var handler = new StubHttpHandler((req, _) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url == "https://mcp.feishu.cn/mcp" && req.Method == HttpMethod.Get)
            {
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }
            if (url == "https://mcp.feishu.cn/mcp" && req.Method == HttpMethod.Post)
            {
                var resp = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
                resp.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    """Bearer realm="mcp", resource_metadata="https://mcp.feishu.cn/.well-known/oauth-protected-resource/mcp", scope="mcp-tool:docs:search-doc offline_access" """);
                return Task.FromResult(resp);
            }
            if (url == "https://mcp.feishu.cn/.well-known/oauth-protected-resource/mcp")
            {
                return Task.FromResult(JsonResponse("""{"resource":"https://mcp.feishu.cn/mcp","authorization_servers":["https://accounts.feishu.cn/mcp"]}"""));
            }
            if (url == "https://accounts.feishu.cn/.well-known/oauth-authorization-server/mcp")
            {
                return Task.FromResult(JsonResponse("""{"issuer":"https://accounts.feishu.cn/mcp","authorization_endpoint":"https://accounts.feishu.cn/oauth/v2/mcp/authorize","token_endpoint":"https://accounts.feishu.cn/oauth/v3/token","registration_endpoint":"https://open.feishu.cn/open-apis/app/v1/dcr","token_endpoint_auth_methods_supported":["none"]}"""));
            }
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        });

        using var http = new HttpClient(handler);
        var oauth = new BotMcpOAuthClient(http, _ => true);
        var meta = await oauth.DiscoverAsync(new Uri("https://mcp.feishu.cn/mcp"));
        Assert.NotNull(meta);
        Assert.Equal("https://accounts.feishu.cn/oauth/v2/mcp/authorize", meta!.AuthorizationEndpoint);
        Assert.Equal("https://accounts.feishu.cn/oauth/v3/token", meta.TokenEndpoint);
        Assert.Equal("https://open.feishu.cn/open-apis/app/v1/dcr", meta.RegistrationEndpoint);
        Assert.Equal("https://mcp.feishu.cn/mcp", meta.Resource);
        Assert.Equal("mcp-tool:docs:search-doc offline_access", meta.Scope);
    }

    private static System.Net.Http.HttpResponseMessage JsonResponse(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private sealed class CallbackDrivingOpener(Func<string, bool> onOpenUrl) : IFileOpener
    {
        public void Reveal(string path) { }
        public void Open(string path) { }
        public bool OpenUrl(string url) => onOpenUrl(url);
    }
}
