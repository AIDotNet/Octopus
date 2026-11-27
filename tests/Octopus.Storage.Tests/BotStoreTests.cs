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
        using var store = new JsonBotStore(Paths());
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
        store.Flush();

        using var reloaded = new JsonBotStore(Paths());
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

    [Fact]
    public void DebouncesUpdatesAndPersistsOnDispose()
    {
        using (var store = new JsonBotStore(Paths()))
        {
            store.Update(d => d.Bots.Add(new BotDefinition { Id = "b1", Name = "First", AgentId = "claude" }));
            store.Update(d => d.Bots[0].DockLayout = "{\"active\":\"c1\"}");
            Assert.Equal("{\"active\":\"c1\"}", store.Current.Bots[0].DockLayout);
        }

        using var reloaded = new JsonBotStore(Paths());
        reloaded.Load();
        Assert.Single(reloaded.Current.Bots);
        Assert.Equal("{\"active\":\"c1\"}", reloaded.Current.Bots[0].DockLayout);
    }

    [Fact]
    public void SequentialPrependOfChineseBots_AssignsSequentialSlugsWithoutRenumberingOrNestedSuffixes()
    {
        using var store = new JsonBotStore(Paths());
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string[] names = ["西瓜", "芒果", "苹果", "香蕉", "葡萄"];
        var expectedById = new Dictionary<string, string>();

        for (var i = 0; i < names.Length; i++)
        {
            var id = $"b{i + 1}";
            var name = names[i];
            var createdAt = baseTime.AddSeconds(i);
            store.Update(d =>
            {
                d.Bots.Insert(0, new BotDefinition
                {
                    Id = id,
                    Name = name,
                    Slug = JsonBotStore.SanitizeSlug(null, name),
                    CreatedAt = createdAt,
                });
            });

            expectedById[id] = i == 0 ? "bot" : $"bot-{i + 1}";

            // Previously created bots must never be renumbered when a new bot is inserted at index 0.
            foreach (var (existingId, expectedSlug) in expectedById)
            {
                var persisted = store.Current.Bots.Single(b => b.Id == existingId);
                Assert.Equal(expectedSlug, persisted.Slug);
            }
        }

        // Document order stays newest-first while slugs remain stably bound to original bots.
        Assert.Equal(["b5", "b4", "b3", "b2", "b1"], store.Current.Bots.Select(b => b.Id).ToArray());
        Assert.Equal(["bot-5", "bot-4", "bot-3", "bot-2", "bot"], store.Current.Bots.Select(b => b.Slug).ToArray());
    }

    [Fact]
    public void DeletingEarlierBot_DoesNotRenumberRemainingBots()
    {
        using var store = new JsonBotStore(Paths());
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        store.Update(d =>
        {
            d.Bots.Add(new BotDefinition { Id = "b3", Name = "苹果", Slug = "bot-3", CreatedAt = baseTime.AddSeconds(2) });
            d.Bots.Add(new BotDefinition { Id = "b2", Name = "芒果", Slug = "bot-2", CreatedAt = baseTime.AddSeconds(1) });
            d.Bots.Add(new BotDefinition { Id = "b1", Name = "西瓜", Slug = "bot", CreatedAt = baseTime });
        });

        store.Update(d => d.Bots.RemoveAll(b => b.Id == "b1"));

        Assert.Equal("bot-3", store.Current.Bots.Single(b => b.Id == "b3").Slug);
        Assert.Equal("bot-2", store.Current.Bots.Single(b => b.Id == "b2").Slug);
    }

    [Fact]
    public void Normalize_PreservesLegacyDistinctSlugsWithoutOrphaningFolders()
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var doc = JsonBotStore.Normalize(new BotsDocument
        {
            Bots =
            [
                new BotDefinition { Id = "4", Name = "GPT-4", Slug = "gpt-4", CreatedAt = baseTime.AddSeconds(3) },
                new BotDefinition { Id = "3", Name = "旧机器人", Slug = "bot-2-2", CreatedAt = baseTime.AddSeconds(2) },
                new BotDefinition { Id = "2", Name = "芒果", Slug = "bot-2", CreatedAt = baseTime.AddSeconds(1) },
                new BotDefinition { Id = "1", Name = "西瓜", Slug = "bot", CreatedAt = baseTime },
            ],
        });

        Assert.Equal("bot", doc.Bots.Single(b => b.Id == "1").Slug);
        Assert.Equal("bot-2", doc.Bots.Single(b => b.Id == "2").Slug);
        Assert.Equal("bot-2-2", doc.Bots.Single(b => b.Id == "3").Slug);
        Assert.Equal("gpt-4", doc.Bots.Single(b => b.Id == "4").Slug);
    }

    [Fact]
    public void Normalize_DuplicateNumericSuffixCollision_IncrementsInsteadOfNesting()
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var doc = JsonBotStore.Normalize(new BotsDocument
        {
            Bots =
            [
                new BotDefinition { Id = "newer", Name = "新机器人", Slug = "bot-2", CreatedAt = baseTime.AddSeconds(10) },
                new BotDefinition { Id = "older", Name = "旧机器人", Slug = "bot-2", CreatedAt = baseTime },
                new BotDefinition { Id = "ten-new", Name = "New Ten", Slug = "my-bot-10", CreatedAt = baseTime.AddSeconds(20) },
                new BotDefinition { Id = "ten-old", Name = "Old Ten", Slug = "my-bot-10", CreatedAt = baseTime.AddSeconds(5) },
            ],
        });

        Assert.Equal("bot-2", doc.Bots.Single(b => b.Id == "older").Slug);
        Assert.Equal("bot-3", doc.Bots.Single(b => b.Id == "newer").Slug);
        Assert.Equal("my-bot-10", doc.Bots.Single(b => b.Id == "ten-old").Slug);
        Assert.Equal("my-bot-2", doc.Bots.Single(b => b.Id == "ten-new").Slug);
    }

    [Fact]
    public void Normalize_ExplicitSlugTakesPriorityOverMissingSlug()
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var doc = JsonBotStore.Normalize(new BotsDocument
        {
            Bots =
            [
                new BotDefinition { Id = "empty", Name = "西瓜", Slug = "", CreatedAt = baseTime },
                new BotDefinition { Id = "explicit", Name = "芒果", Slug = "bot", CreatedAt = baseTime.AddSeconds(5) },
            ],
        });

        Assert.Equal("bot", doc.Bots.Single(b => b.Id == "explicit").Slug);
        Assert.Equal("bot-2", doc.Bots.Single(b => b.Id == "empty").Slug);
    }

    [Fact]
    public void AllocateUniqueSlug_HandlesNonAsciiCaseInsensitiveAndGaps()
    {
        Assert.Equal("bot", JsonBotStore.AllocateUniqueSlug(null, "西瓜", []));
        Assert.Equal("pr-reviewer", JsonBotStore.AllocateUniqueSlug(null, "PR Reviewer", []));
        Assert.Equal("bot-2", JsonBotStore.AllocateUniqueSlug(null, "西瓜", ["BOT"]));
        Assert.Equal("bot-3", JsonBotStore.AllocateUniqueSlug("bot-2", "西瓜", ["bot", "bot-2"]));
        Assert.Equal("bot-2", JsonBotStore.AllocateUniqueSlug(null, "西瓜", ["bot", "bot-3"]));
        Assert.Equal("bot-4", JsonBotStore.AllocateUniqueSlug(null, "西瓜", ["bot", "bot-2", "bot-3"]));
        Assert.Equal("gpt-1-2", JsonBotStore.AllocateUniqueSlug("gpt-1", "GPT-1", ["gpt-1"]));
        Assert.Equal("my-bot-2", JsonBotStore.AllocateUniqueSlug("my-bot-10", "My Bot", ["my-bot-10"]));
        Assert.Equal("my-bot-3", JsonBotStore.AllocateUniqueSlug("my-bot-10", "My Bot", ["my-bot-2", "my-bot-10"]));
    }
}
