using Octopus.Core.Plugins;
using Octopus.Storage;
using Xunit;

namespace Octopus.Storage.Tests;

public class PluginStateStoreTests
{
    [Fact]
    public void Round_trips_records_and_grants()
    {
        using var paths = new TempPaths();
        var store = new JsonPluginStateStore(paths);
        store.Update(d => d.Plugins["acme.csv"] = new PluginRecord
        {
            Enabled = true,
            Version = "1.2.0",
            Source = PluginSource.File,
            InstalledAt = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero),
            Grants = { ["fs.project"] = "read", ["agents.signals.claude.statusline"] = "granted" },
            PathGrants = { new PluginPathGrant("agentData", "/Users/张三/.claude", FileAccessKind.Read, "claude") },
            Denied = { "net" },
        });

        var reread = new JsonPluginStateStore(paths);
        reread.Load();
        var record = reread.Current.Find("acme.csv")!;

        Assert.True(record.Enabled);
        Assert.Equal(PluginSource.File, record.Source);
        Assert.Equal("read", record.Grants["fs.project"]);
        Assert.Equal(new PluginPathGrant("agentData", "/Users/张三/.claude", FileAccessKind.Read, "claude"), Assert.Single(record.PathGrants));
        Assert.Equal(["net"], record.Denied);
        var text = File.ReadAllText(Path.Combine(paths.DataDirectory, "plugins.json"));
        Assert.Contains("\"source\": \"file\"", text);
        Assert.Contains("/Users/张三/.claude", text);
    }

    [Fact]
    public void A_damaged_file_is_set_aside_and_nothing_stays_granted()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        var file = Path.Combine(paths.DataDirectory, "plugins.json");
        File.WriteAllText(file, "{ \"plugins\": { \"acme.csv\": { \"enabled\": true, \"grants\": ");

        var store = new JsonPluginStateStore(paths);
        store.Load();

        Assert.Empty(store.Current.Plugins);
        Assert.False(File.Exists(file));
        Assert.Single(Directory.GetFiles(paths.DataDirectory, "plugins.json.corrupt-*"));
    }

    [Fact]
    public void Missing_parts_of_a_hand_edited_file_are_filled_in()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(Path.Combine(paths.DataDirectory, "plugins.json"),
            """{ "version": 1, "plugins": { "acme.csv": { "enabled": true, "grants": null, "crashes": ["2026-11-01T00:00:00Z","2026-11-02T00:00:00Z","2026-11-03T00:00:00Z","2026-11-04T00:00:00Z","2026-11-05T00:00:00Z","2026-11-06T00:00:00Z","2026-11-07T00:00:00Z","2026-11-08T00:00:00Z","2026-11-09T00:00:00Z","2026-11-10T00:00:00Z","2026-11-11T00:00:00Z"] }, "": { "enabled": true } } }""");

        var store = new JsonPluginStateStore(paths);
        store.Load();

        var record = Assert.Single(store.Current.Plugins).Value;
        Assert.NotNull(record.Grants);
        Assert.Equal(JsonPluginStateStore.MaxCrashesKept, record.Crashes.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), record.Crashes[^1]);
    }
}
