using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.App.Bots;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Explorer.Files;
using Octopus.App.Explorer.Search;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Platform;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

public sealed class BotDefaultModelTests
{
    private sealed class FakeModelDiscovery(Func<AgentDefinition, IReadOnlyList<AgentOption>> handler) : IModelDiscovery
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<AgentOption>> DiscoverAsync(
            AgentDefinition definition,
            string executable,
            bool refresh = false,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environmentOverrides = null)
        {
            Calls++;
            return Task.FromResult(handler(definition));
        }
    }

    private sealed class StubAgentCatalog(IReadOnlyList<AgentDefinition> definitions) : IAgentCatalog
    {
        public IReadOnlyList<AgentDefinition> Definitions { get; } = definitions;
        public AgentDefinition? Find(string agentId) =>
            Definitions.FirstOrDefault(d => string.Equals(d.Id, agentId, StringComparison.OrdinalIgnoreCase));
        public void Reload() { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class StubAgentScanner(IReadOnlyList<AgentInstallation> installations) : IAgentScanner
    {
        public AgentScanResult Current { get; } = new() { Installations = [.. installations] };
        public bool IsScanning => false;
        public event EventHandler? Changed { add { } remove { } }
        public Task LoadCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public AgentInstallation? Find(string agentId) =>
            Current.Installations.FirstOrDefault(i => string.Equals(i.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void CreateBot_WithExplicitModel_PersistsModelAndPopulatesLaunchOptions()
    {
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("模型测试", "claude", "#5b5bd6", "blank", null, "local", null, "  claude-sonnet-4-6  "));

        Assert.Equal("claude-sonnet-4-6", bot.Model);

        // Home chat inherits bot.Model through ToProjectItem
        var backing = bots.GetBackingProject(bot);
        var homeItem = backing.Items.Single(i => i.Id == bot.HomeChatId);
        Assert.NotNull(homeItem.Launch);
        Assert.Equal("claude-sonnet-4-6", homeItem.Launch!.Model);

        // New chat with default agent inherits bot.Model
        var newChat = bots.CreateChat(bot.Id)!;
        var newChatItem = backing.Items.Single(i => i.Id == newChat.Id);
        Assert.Equal("claude-sonnet-4-6", newChatItem.Launch?.Model);

        // New chat with a different agent must NOT receive the bot's Claude model
        var otherAgentChat = bots.CreateChat(bot.Id, "codex", "Codex Chat")!;
        var otherAgentItem = backing.Items.Single(i => i.Id == otherAgentChat.Id);
        Assert.Null(otherAgentItem.Launch?.Model);

        // Group member chat inherits bot.Model
        var groupChat = bots.CreateGroupMemberChat(bot.Id, "grp-1", null, "Room")!;
        var groupItem = bots.GetGroupMemberBackingItem(bot.Id, groupChat.Id);
        Assert.NotNull(groupItem?.Launch);
        Assert.Equal("claude-sonnet-4-6", groupItem!.Launch!.Model);
    }

    [AvaloniaFact]
    public void CreateBot_WithoutModel_LeavesModelNullAndFollowsAgentDefault()
    {
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("默认模型", "claude", "#5b5bd6", "blank", null, "local", null, "   "));

        Assert.Null(bot.Model);
        var backing = bots.GetBackingProject(bot);
        var homeItem = backing.Items.Single(i => i.Id == bot.HomeChatId);
        Assert.Null(homeItem.Launch);

        var chat = bots.CreateChat(bot.Id)!;
        var chatItem = backing.Items.Single(i => i.Id == chat.Id);
        Assert.Null(chatItem.Launch);
    }

    [AvaloniaFact]
    public void UpdateBotSettings_ModelChange_SyncsBackingProjectAndGroupItems()
    {
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("动态切换", "claude", "#5b5bd6", "blank", null));
        var groupChat = bots.CreateGroupMemberChat(bot.Id, "grp-2", null, "Room")!;
        var groupItem = bots.GetGroupMemberBackingItem(bot.Id, groupChat.Id)!;
        var backing = bots.GetBackingProject(bot);
        var homeItem = backing.Items.Single(i => i.Id == bot.HomeChatId);

        Assert.Null(homeItem.Launch);
        Assert.Null(groupItem.Launch);

        bots.UpdateBotSettings(bot.Id, b => b.Model = "  claude-opus-4-6 ");
        Assert.Equal("claude-opus-4-6", bots.Bots.Single(b => b.Id == bot.Id).Model);
        Assert.Equal("claude-opus-4-6", homeItem.Launch?.Model);
        Assert.Equal("claude-opus-4-6", groupItem.Launch?.Model);

        bots.UpdateBotSettings(bot.Id, b => b.Model = "   ");
        Assert.Null(bots.Bots.Single(b => b.Id == bot.Id).Model);
        Assert.Null(homeItem.Launch);
        Assert.Null(groupItem.Launch);
    }

    [AvaloniaFact]
    public void ShareCodec_AndImportBot_RoundTripDefaultModel()
    {
        var bots = AppHost.Get<IBotService>();
        var source = bots.CreateBot(new NewBotRequest("分享机器人", "claude", "#2f9e63", "blank", "负责代码审查", "local", null, "claude-sonnet-4-6"));

        var payload = BotShareCodec.BuildPayload(source, bots.Home, includeMemory: false);
        Assert.Equal("claude-sonnet-4-6", payload.Model);

        var encoded = BotShareCodec.Encode(payload);
        Assert.True(BotShareCodec.TryDecodeString(encoded, out var decoded, out var error), error);
        Assert.NotNull(decoded);
        Assert.Equal("claude-sonnet-4-6", decoded!.Model);

        var imported = bots.ImportBot(decoded);
        Assert.Equal("claude-sonnet-4-6", imported.Model);

        var backing = bots.GetBackingProject(imported);
        var homeItem = backing.Items.Single(i => i.Id == imported.HomeChatId);
        Assert.Equal("claude-sonnet-4-6", homeItem.Launch?.Model);
    }

    [AvaloniaFact]
    public async Task BotRightPanel_PopulatesModels_PersistsSelection_ClearsOnAgentSwitch_AndDiscoversAsync()
    {
        var bots = AppHost.Get<IBotService>();
        var dialogs = AppHost.Get<IDialogService>();
        var catalog = AppHost.Get<IAgentCatalog>();
        var opener = AppHost.Get<IFileOpener>();
        var files = AppHost.Get<FileTreeViewModel>();
        var search = AppHost.Get<FileSearchViewModel>();
        var scanner = AppHost.Get<IAgentScanner>();
        var settings = AppHost.Get<ISettingsStore>();

        var discovery = new FakeModelDiscovery(def =>
            string.Equals(def.Id, "claude", StringComparison.OrdinalIgnoreCase)
                ? [new AgentOption { Id = "claude-discovered-test", Label = "Claude Discovered" }]
                : []);

        var bot = bots.CreateBot(new NewBotRequest("配置测试", "claude", "#5b5bd6", "blank", null));

        using var right = new BotRightPanelViewModel(
            bots, dialogs, catalog, opener, files, search,
            scanner: scanner, settings: settings, modelDiscovery: discovery);

        right.OpenSettingsCommand.Execute(null);
        await WaitUntilAsync(() => right.ModelOptions.Any(o => Equals(o.Value, "claude-discovered-test")));

        Assert.True(right.HasModelOptions);
        Assert.Equal("", right.ModelOptions[0].Value);
        Assert.Contains(right.ModelOptions, o => Equals(o.Value, "claude-discovered-test"));

        // Selecting a model persists to bot.Model
        right.SelectedModelId = "claude-discovered-test";
        Assert.Equal("claude-discovered-test", bots.Bots.Single(b => b.Id == bot.Id).Model);

        // Switching agent clears bot.Model so Claude's model does not leak onto Codex
        right.SelectedAgentId = "codex";
        var updated = bots.Bots.Single(b => b.Id == bot.Id);
        Assert.Equal("codex", updated.AgentId);
        Assert.Null(updated.Model);
        Assert.Equal("", right.SelectedModelId);

        // Saved custom model outside the catalog remains selectable
        bots.UpdateBotSettings(bot.Id, b => b.Model = "custom-codex-model");
        right.Refresh();
        Assert.Equal("custom-codex-model", right.SelectedModelId);
        Assert.Contains(right.ModelOptions, o => Equals(o.Value, "custom-codex-model"));

        // Resetting back to follow-agent clears bot.Model
        right.SelectedModelId = "";
        Assert.Null(bots.Bots.Single(b => b.Id == bot.Id).Model);
    }

    [AvaloniaFact]
    public void BotRightPanel_HidesModelSelector_WhenAgentHasNoModelCapability()
    {
        var noModelAgent = new AgentDefinition
        {
            Id = "plain-cli",
            Name = "Plain CLI",
            Capabilities = new AgentCapabilities { Model = null },
        };
        var catalog = new StubAgentCatalog([noModelAgent]);
        var scanner = new StubAgentScanner([new AgentInstallation { AgentId = "plain-cli", Path = "/usr/bin/plain-cli" }]);
        var settings = AppHost.Get<ISettingsStore>();
        var paths = AppHost.Get<PathComparer>();
        var store = new InMemoryBotStore();
        var tempHome = Directory.CreateTempSubdirectory("octopus-bot-nomodel-").FullName;
        try
        {
            var bots = new BotService(store, catalog, scanner, settings, paths, new BotHomeManager(new BotRootOptions(tempHome)));
            bots.CreateBot(new NewBotRequest("无模型", "plain-cli", "#5b5bd6", "blank", null));

            using var right = new BotRightPanelViewModel(
                bots,
                AppHost.Get<IDialogService>(),
                catalog,
                AppHost.Get<IFileOpener>(),
                AppHost.Get<FileTreeViewModel>(),
                AppHost.Get<FileSearchViewModel>(),
                scanner: scanner,
                settings: settings);

            Assert.False(right.HasModelOptions);
            Assert.Empty(right.ModelOptions);
            Assert.Equal("", right.SelectedModelId);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task NewBotDialog_PopulatesModels_ResetsOnAgentSwitch_BuildsRequest_AndRendersCleanly()
    {
        var catalog = AppHost.Get<IAgentCatalog>();
        var scanner = new StubAgentScanner(
        [
            new AgentInstallation { AgentId = "claude", Path = "/usr/local/bin/claude" },
            new AgentInstallation { AgentId = "codex", Path = "/usr/local/bin/codex" },
        ]);
        var settings = AppHost.Get<ISettingsStore>();
        var discovery = new FakeModelDiscovery(def =>
            string.Equals(def.Id, "claude", StringComparison.OrdinalIgnoreCase)
                ? [new AgentOption { Id = "claude-from-discovery", Label = "Claude From Discovery" }]
                : [new AgentOption { Id = "codex-from-discovery", Label = "Codex From Discovery" }]);

        using var vm = new NewBotDialogViewModel(
            catalog, scanner, settings,
            initialTemplateId: "blank",
            modelDiscovery: discovery);

        vm.SelectedAgentId = "claude";
        await WaitUntilAsync(() => vm.ModelOptions.Any(o => Equals(o.Value, "claude-from-discovery")));

        Assert.True(vm.HasModelOptions);
        Assert.Equal("", vm.ModelOptions[0].Value);
        Assert.Equal("", vm.SelectedModelId);
        Assert.Null(vm.BuildRequest().Model);

        vm.SelectedModelId = "claude-from-discovery";
        Assert.Equal("claude-from-discovery", vm.BuildRequest().Model);

        // Switching agent resets model selection to follow-agent default and discovers for the new agent
        vm.SelectedAgentId = "codex";
        Assert.Equal("", vm.SelectedModelId);
        Assert.Null(vm.BuildRequest().Model);
        await WaitUntilAsync(() => vm.ModelOptions.Any(o => Equals(o.Value, "codex-from-discovery")));
        Assert.Contains(vm.ModelOptions, o => Equals(o.Value, "codex-from-discovery"));

        // Verify NewBotDialogView binds cleanly to ModelSelect
        var window = new Window { Width = 520, Height = 620, Content = new NewBotDialogView(vm) };
        window.Show();
        try
        {
            Assert.True(vm.HasModelOptions);
        }
        finally
        {
            window.Close();
        }
    }
}
