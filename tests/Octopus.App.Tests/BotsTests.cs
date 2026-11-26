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

        // Filter by coding category (8 apps matching reference design)
        var coding = catalog.Categories.Single(c => c.Id == "coding");
        Assert.Equal(8, coding.Count);
        coding.SelectCommand.Execute(null);
        Assert.Single(catalog.Groups);
        Assert.Equal(8, catalog.Groups[0].Apps.Count);

        // Add GitHub from catalog and add custom URL connector
        catalog.Groups[0].Apps.Single(a => a.AppId == "github").ToggleCommand.Execute(null);
        catalog.CustomUrlName = "Local MCP";
        catalog.CustomUrl = "https://mcp.example.com/sse";
        catalog.AddByUrlCommand.Execute(null);

        Assert.True(right.HasConnectors);
        Assert.Equal(2, right.Connectors.Count);

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
        var window = new Window { Width = 680, Height = 520, Content = new ConnectorCatalogView(catalogVm) };
        window.Show();
        window.Close();
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
}
