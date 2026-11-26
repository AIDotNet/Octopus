using Octopus.Platform;
using Xunit;

namespace Octopus.Storage.Tests;

public class StarPromptStorageTests
{
    private sealed class TestPaths(string dataDir) : IAppPaths
    {
        public string DataDirectory => dataDir;
        public string CacheDirectory => Path.Combine(dataDir, "cache");
    }

    [Fact]
    public void StarPromptSettingsRoundTripAndNormalize()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-star-storage-");
        try
        {
            var paths = new TestPaths(dir.FullName);
            var path = Path.Combine(dir.FullName, "settings.json");
            var firstSeen = new DateTimeOffset(2026, 11, 20, 8, 30, 0, TimeSpan.Zero);
            var snoozeUntil = new DateTimeOffset(2026, 12, 9, 10, 0, 0, TimeSpan.Zero);

            using (var store = new JsonSettingsStore(paths))
            {
                store.Update(s =>
                {
                    s.StarPrompt.FirstSeenUtc = firstSeen;
                    s.StarPrompt.LastActiveDate = "2026-11-25";
                    s.StarPrompt.ActiveDaysCount = 4;
                    s.StarPrompt.CompletedAgentTurns = 7;
                    s.StarPrompt.SessionStartCount = 11;
                    s.StarPrompt.PromptCount = 1;
                    s.StarPrompt.SnoozeUntilUtc = snoozeUntil;
                    s.StarPrompt.Starred = false;
                    s.StarPrompt.DismissedForever = false;
                });
                store.Flush();
            }

            using (var reloaded = new JsonSettingsStore(paths))
            {
                reloaded.Load();
                var p = reloaded.Current.StarPrompt;
                Assert.Equal(firstSeen, p.FirstSeenUtc);
                Assert.Equal("2026-11-25", p.LastActiveDate);
                Assert.Equal(4, p.ActiveDaysCount);
                Assert.Equal(7, p.CompletedAgentTurns);
                Assert.Equal(11, p.SessionStartCount);
                Assert.Equal(1, p.PromptCount);
                Assert.Equal(snoozeUntil, p.SnoozeUntilUtc);
                Assert.False(p.Starred);
                Assert.False(p.DismissedForever);
            }

            // Corrupted / negative counters normalize safely to 0.
            File.WriteAllText(path, """
            {
              "starPrompt": {
                "activeDaysCount": -3,
                "completedAgentTurns": -1,
                "sessionStartCount": -5,
                "promptCount": -2,
                "lastActiveDate": "   "
              }
            }
            """);

            using var normalized = new JsonSettingsStore(paths);
            normalized.Load();
            Assert.Equal(0, normalized.Current.StarPrompt.ActiveDaysCount);
            Assert.Equal(0, normalized.Current.StarPrompt.CompletedAgentTurns);
            Assert.Equal(0, normalized.Current.StarPrompt.SessionStartCount);
            Assert.Equal(0, normalized.Current.StarPrompt.PromptCount);
            Assert.Null(normalized.Current.StarPrompt.LastActiveDate);
        }
        finally
        {
            try { dir.Delete(true); } catch (IOException) { }
        }
    }
}
