using Octopus.Core.Bots;
using Octopus.Platform;
using Xunit;

namespace Octopus.Storage.Tests;

public sealed class BotStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("octopus-bot-store-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private FixedAppPaths Paths() => new(_dir);

    [Fact]
    public void RoundTripsBotsChatsConnectorsAndWorkItems()
    {
        var store = new JsonBotStore(Paths());
        store.Update(d =>
        {
            d.IsBotMode = true;
            var bot = new BotDefinition
            {
                Id = "b1",
                Name = "西瓜",
                Slug = "",
                AgentId = "claude",
                Color = "#2f9e63",
                Soul = "帮我盯住 PR 和 CI",
                HomeChatId = "c1",
                ActiveChatId = "c1",
                Chats =
                [
                    new BotChat { Id = "c1", Kind = BotChatKind.Home, Title = "Home Chat", Status = BotChatStatus.Idle },
                    new BotChat { Id = "c2", Kind = BotChatKind.Chat, Title = "Claude Chat", Status = BotChatStatus.Closed, IsOpen = false },
                ],
                Connectors =
                [
                    new BotConnectorBinding { Id = "k1", AppId = "github", DisplayName = "GitHub", Category = "coding" },
                ],
                Items =
                [
                    new BotWorkItem { Id = "i1", Number = 1, Title = "Fix relay", Status = BotWorkItemStatus.NeedsYou, Options = ["Merge", "Hold"] },
                ],
            };
            d.Bots.Add(bot);
            d.ActiveBotId = bot.Id;
        });

        var reloaded = new JsonBotStore(Paths());
        reloaded.Load();

        Assert.True(reloaded.Current.IsBotMode);
        Assert.Single(reloaded.Current.Bots);
        var loaded = reloaded.Current.Bots[0];
        Assert.Equal("西瓜", loaded.Name);
        Assert.Equal("bot", loaded.Slug);
        Assert.Equal(2, loaded.Chats.Count);
        Assert.Equal(BotChatStatus.Closed, loaded.Chats[1].Status);
        Assert.Single(loaded.Connectors);
        Assert.Single(loaded.Items);
        Assert.Equal(["Merge", "Hold"], loaded.Items[0].Options);
    }

    [Fact]
    public void AssignsUniqueSlugsForNonAsciiAndDuplicateNames()
    {
        var doc = JsonBotStore.Normalize(new BotsDocument
        {
            Bots =
            [
                new BotDefinition { Id = "1", Name = "西瓜", Slug = "" },
                new BotDefinition { Id = "2", Name = "芒果", Slug = "" },
                new BotDefinition { Id = "3", Name = "PR Reviewer", Slug = "" },
                new BotDefinition { Id = "4", Name = "PR Reviewer", Slug = "" },
            ],
        });

        Assert.Equal(["bot", "bot-2", "pr-reviewer", "pr-reviewer-2"], doc.Bots.Select(b => b.Slug).ToArray());
    }
}
