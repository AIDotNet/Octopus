using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.App.Bots;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Shell.Keymap;
using Octopus.Core.Bots;
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
}
