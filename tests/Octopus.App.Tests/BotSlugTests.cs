using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Octopus.App.Bots;
using Octopus.App.Composition;
using Octopus.Core.Bots;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

public sealed class BotSlugTests
{
    [Fact]
    public void GetOccupiedSlugsOnDisk_ReturnsEmptyWhenRootDirectoryDoesNotExist()
    {
        var tempHome = Directory.CreateTempSubdirectory("octopus-bot-home-empty-").FullName;
        try
        {
            var home = new BotHomeManager(new BotRootOptions(tempHome));
            Assert.Empty(home.GetOccupiedSlugsOnDisk());
            Assert.Empty(home.GetOccupiedSlugsOnDisk("any-bot-id"));
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void GetOccupiedSlugsOnDisk_ExcludesMatchingBotIdAndKeepsForeignOrBrokenFolders()
    {
        var tempHome = Directory.CreateTempSubdirectory("octopus-bot-home-disk-").FullName;
        try
        {
            var home = new BotHomeManager(new BotRootOptions(tempHome));
            var botA = new BotDefinition { Id = "bot-a", Name = "西瓜", Slug = "bot" };
            var botB = new BotDefinition { Id = "bot-b", Name = "芒果", Slug = "bot-2" };
            home.EnsureHome(botA);
            home.EnsureHome(botB);

            // Folder without bot.json and folder with corrupt bot.json must both be treated as occupied.
            Directory.CreateDirectory(Path.Combine(home.BotsRootDirectory, "orphan"));
            var corruptDir = Path.Combine(home.BotsRootDirectory, "corrupt");
            Directory.CreateDirectory(corruptDir);
            File.WriteAllText(Path.Combine(corruptDir, "bot.json"), "{ not valid json", Encoding.UTF8);

            // Loose file in bots root must be ignored.
            File.WriteAllText(Path.Combine(home.BotsRootDirectory, "README.txt"), "ignore me", Encoding.UTF8);

            var all = home.GetOccupiedSlugsOnDisk();
            Assert.Equal(4, all.Count);
            Assert.Contains("bot", all);
            Assert.Contains("bot-2", all);
            Assert.Contains("orphan", all);
            Assert.Contains("corrupt", all);

            var excludingA = home.GetOccupiedSlugsOnDisk("bot-a");
            Assert.Equal(3, excludingA.Count);
            Assert.DoesNotContain("bot", excludingA);
            Assert.Contains("bot-2", excludingA);
            Assert.Contains("orphan", excludingA);
            Assert.Contains("corrupt", excludingA);

            // Case-insensitive allocation avoids occupied disk folders.
            Assert.Equal("bot-3", JsonBotStore.AllocateUniqueSlug(null, "西瓜", all));
            Assert.Equal("bot", JsonBotStore.AllocateUniqueSlug(null, "西瓜", excludingA));
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public void CreateFiveChineseBots_PreservesSlugsAndIsolatesWorkspacesAndMemory()
    {
        var bots = AppHost.Get<IBotService>();
        string[] names = ["西瓜", "芒果", "苹果", "香蕉", "葡萄"];
        var created = new List<(string Id, string ExpectedSlug, string SoulMarker, string MemoryMarker)>();

        for (var i = 0; i < names.Length; i++)
        {
            var soulMarker = $"SOUL_MARKER_{i + 1}_{names[i]}";
            var memoryMarker = $"MEMORY_MARKER_{i + 1}_{names[i]}";
            var bot = bots.CreateBot(new NewBotRequest(names[i], "claude", "#2f9e63", "blank", soulMarker));
            var expectedSlug = i == 0 ? "bot" : $"bot-{i + 1}";

            Assert.Equal(expectedSlug, bot.Slug);
            File.AppendAllText(bots.Home.GetMemoryPath(bot), $"\n- {memoryMarker}\n", Encoding.UTF8);
            created.Add((bot.Id, expectedSlug, soulMarker, memoryMarker));

            // Every previously created bot must retain its original slug and isolated files.
            foreach (var entry in created)
            {
                var current = bots.Bots.Single(b => b.Id == entry.Id);
                Assert.Equal(entry.ExpectedSlug, current.Slug);
                Assert.DoesNotContain("-2-2", current.Slug, StringComparison.Ordinal);

                var manifestJson = File.ReadAllText(bots.Home.GetManifestPath(current), Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize(manifestJson, OctopusJsonContext.FileTypeInfo<BotHomeManifest>());
                Assert.NotNull(manifest);
                Assert.Equal(entry.Id, manifest!.Id);
                Assert.Equal(entry.ExpectedSlug, manifest.Slug);

                var soulText = File.ReadAllText(bots.Home.GetSoulPath(current), Encoding.UTF8);
                Assert.Contains(entry.SoulMarker, soulText);

                var memoryText = File.ReadAllText(bots.Home.GetMemoryPath(current), Encoding.UTF8);
                Assert.Contains(entry.MemoryMarker, memoryText);
            }
        }
    }

    [AvaloniaFact, FreshApp]
    public void ImportBot_AndDeleteBot_PreserveSlugsAndSkipOccupiedDiskFolders()
    {
        var bots = AppHost.Get<IBotService>();

        var first = bots.CreateBot(new NewBotRequest("西瓜", "claude", "#2f9e63", "blank", "FIRST_SOUL"));
        var second = bots.CreateBot(new NewBotRequest("芒果", "claude", "#3b82d6", "blank", "SECOND_SOUL"));
        Assert.Equal("bot", first.Slug);
        Assert.Equal("bot-2", second.Slug);

        var imported = bots.ImportBot(new BotSharePayload
        {
            Name = "巡检官",
            AgentId = "claude",
            SoulMarkdown = "# 巡检官\nIMPORTED_SOUL",
            MemoryMarkdown = "# Memory\n- IMPORTED_MEMORY",
        });

        Assert.Equal("bot-3", imported.Slug);
        Assert.Equal("bot", bots.Bots.Single(b => b.Id == first.Id).Slug);
        Assert.Equal("bot-2", bots.Bots.Single(b => b.Id == second.Id).Slug);
        Assert.Contains("FIRST_SOUL", File.ReadAllText(bots.Home.GetSoulPath(first), Encoding.UTF8));
        Assert.Contains("SECOND_SOUL", File.ReadAllText(bots.Home.GetSoulPath(second), Encoding.UTF8));
        Assert.Contains("IMPORTED_SOUL", File.ReadAllText(bots.Home.GetSoulPath(imported), Encoding.UTF8));

        // Deleting the first bot must not renumber remaining bots.
        bots.DeleteBot(first.Id);
        Assert.Equal("bot-2", bots.Bots.Single(b => b.Id == second.Id).Slug);
        Assert.Equal("bot-3", bots.Bots.Single(b => b.Id == imported.Id).Slug);

        // Because the deleted bot's workspace directory ("bot") still exists on disk, a new Chinese bot must not reuse it.
        Assert.True(Directory.Exists(bots.Home.GetHomePath(first)));
        var fourth = bots.CreateBot(new NewBotRequest("新机器人", "claude", "#5b5bd6", "blank", "FOURTH_SOUL"));
        Assert.Equal("bot-4", fourth.Slug);
        Assert.Contains("FIRST_SOUL", File.ReadAllText(bots.Home.GetSoulPath(first), Encoding.UTF8));
        Assert.Contains("FOURTH_SOUL", File.ReadAllText(bots.Home.GetSoulPath(fourth), Encoding.UTF8));
    }
}
