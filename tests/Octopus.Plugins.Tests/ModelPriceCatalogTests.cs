using System.Text;
using Octopus.Plugins.Usage;
using Xunit;

namespace Octopus.Plugins.Tests;

/// <summary>The host's model prices: synced from a models.dev catalog, saved, and looked up by the ids agents write.</summary>
public sealed class ModelPriceCatalogTests
{
    /// <summary>A small models.dev catalog: a vendor and resellers disagreeing, a model only resellers list, a tier.</summary>
    private const string Catalog = """
        {
          "requesty": { "models": {
            "claude-sonnet-5-5": { "cost": { "input": 0.7, "output": 3.5 } },
            "mimo-v2.5-pro": { "cost": { "input": 0.435, "output": 0.87 } } } },
          "anthropic": { "models": {
            "claude-sonnet-5-5": { "cost": { "input": 2, "output": 10, "cache_read": 0.1, "cache_write": 2.5 } } } },
          "teapot": { "models": {
            "xiaomi/mimo-v2.5-pro": { "cost": { "input": 0.435, "output": 0.87 } },
            "free/mimo-v2.5-pro": { "cost": { "input": 0, "output": 0 } } } },
          "digitalocean": { "models": { "mimo-v2.5-pro": { "cost": { "input": 0.8, "output": 3 } } } },
          "openai": { "models": {
            "gpt-6.1-sol": { "cost": { "input": 2, "output": 10, "cache_read": 0.1, "cache_write": 2.5,
              "tiers": [{ "input": 4, "output": 15, "cache_read": 0.2, "cache_write": 5, "tier": { "type": "context", "size": 272000 } }] } },
            "gpt-image-9": { "name": "no cost listed" } } }
        }
        """;

    private static Func<CancellationToken, Task<Stream>> Serve(string json, Action? onCall = null) => _ =>
    {
        onCall?.Invoke();
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    };

    [Fact]
    public async Task Sync_PricesEachModelByItsVendor_ElseByWhatMostResellersAsk()
    {
        using var paths = new TempAppPaths();
        using var catalog = new ModelPriceCatalog(new PluginPaths(paths), Serve(Catalog));
        await catalog.EnsureFreshAsync();
        Assert.Null(catalog.LastError);
        Assert.NotNull(catalog.SyncedAt);

        // Anthropic's own price wins over a reseller's.
        Assert.Equal(new ModelPrice(2, 10, 0.1, 2.5), catalog.Find("claude-sonnet-5-5"));
        // No vendor in the catalog: the price most resellers agree on; a free plan's 0 does not count, routes are dropped.
        Assert.Equal(new ModelPrice(0.435, 0.87, 0.435, 0.435), catalog.Find("mimo-v2.5-pro"));
        // A context tier is kept: GPT-6.1 Sol costs more past 272K.
        Assert.Equal(new ModelPriceTier(272000, 4, 15, 0.2, 5), catalog.Find("gpt-6.1-sol")!.Tier);
        Assert.Equal(3, catalog.Count);
    }

    [Theory]
    [InlineData("claude-sonnet-5.5")]                   // Claude Code writes both spellings
    [InlineData("anthropic/claude-sonnet-5-5")]         // a route in front
    [InlineData("claude-sonnet-5-5[1m]")]               // a variant behind
    [InlineData("claude-sonnet-5-5-20260928")]          // a release date
    [InlineData("claude-sonnet-5-5-medium")]            // Antigravity CLI effort suffix
    [InlineData("claude-sonnet-5.5-high")]              // dot spelling + effort suffix
    [InlineData("CLAUDE-SONNET-5-5")]
    public async Task Find_ReadsTheIdsAgentsWrite(string id)
    {
        using var paths = new TempAppPaths();
        using var catalog = new ModelPriceCatalog(new PluginPaths(paths), Serve(Catalog));
        await catalog.EnsureFreshAsync();
        Assert.Equal(2, catalog.Find(id)?.Input);
    }

    [Fact]
    public async Task Find_ResolvesAgyEffortAndGrokBuildModelIds()
    {
        const string extraCatalog = """
            {
              "google": { "models": {
                "gemini-4-neon": { "cost": { "input": 3, "output": 15, "cache_read": 0.15 } },
                "gemini-3-8-flash": { "cost": { "input": 0.5, "output": 2, "cache_read": 0.05 } } } },
              "xai": { "models": {
                "grok-4-7": { "cost": { "input": 2, "output": 6, "cache_read": 0.5 } } } }
            }
            """;
        using var paths = new TempAppPaths();
        using var catalog = new ModelPriceCatalog(new PluginPaths(paths), Serve(extraCatalog));
        await catalog.EnsureFreshAsync();

        Assert.Equal(3, catalog.Find("gemini-4-neon-xhigh")?.Input);
        Assert.Equal(3, catalog.Find("gemini-4-neon-low")?.Input);
        Assert.Equal(0.5, catalog.Find("gemini-3.8-flash-high")?.Input);
        Assert.Equal(2, catalog.Find("grok-4.7-build")?.Input);
        Assert.Equal(2, catalog.Find("grok-4.7-build-fast")?.Input);
    }

    [Fact]
    public async Task TheIndex_IsSaved_AndASecondStartDoesNotSyncWithinADay()
    {
        using var paths = new TempAppPaths();
        var calls = 0;
        using (var first = new ModelPriceCatalog(new PluginPaths(paths), Serve(Catalog, () => calls++)))
        {
            await first.EnsureFreshAsync();
        }
        using var second = new ModelPriceCatalog(new PluginPaths(paths), Serve(Catalog, () => calls++));
        await second.EnsureFreshAsync();
        Assert.Equal(1, calls);
        Assert.Equal(0.435, second.Find("mimo-v2.5-pro")?.Input);
        Assert.Equal(272000, second.Find("gpt-6.1-sol")?.Tier?.Size);

        // A day later it asks again; forcing asks at once.
        using var later = new ModelPriceCatalog(new PluginPaths(paths), Serve(Catalog, () => calls++), () => DateTimeOffset.UtcNow.AddDays(1.1));
        await later.EnsureFreshAsync();
        await later.EnsureFreshAsync(force: true);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task WithoutACatalog_TheBuiltInTableStandsIn_AndAFailureIsReported()
    {
        using var paths = new TempAppPaths();
        using var catalog = new ModelPriceCatalog(new PluginPaths(paths), _ => throw new HttpRequestException("offline"));
        await catalog.EnsureFreshAsync();
        Assert.Equal("offline", catalog.LastError);
        Assert.Null(catalog.SyncedAt);
        Assert.Equal(new ModelPrice(4, 20, 0.2, 5), catalog.Find("claude-opus-5-5"));
        Assert.Null(catalog.Find("mimo-v2.5-pro"));
    }

    [Fact]
    public async Task ASyncThatChangesAPrice_RaisesChanged_AndOneThatDoesNotStaysQuiet()
    {
        using var paths = new TempAppPaths();
        var json = Catalog;
        using var catalog = new ModelPriceCatalog(new PluginPaths(paths), _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(json))));
        var changes = 0;
        catalog.Changed += (_, _) => changes++;
        await catalog.EnsureFreshAsync();
        await catalog.EnsureFreshAsync(force: true);
        Assert.Equal(1, changes);
        json = Catalog.Replace("\"input\": 2, \"output\": 10, \"cache_read\": 0.1", "\"input\": 1.5, \"output\": 10, \"cache_read\": 0.1");
        await catalog.EnsureFreshAsync(force: true);
        Assert.Equal(2, changes);
        Assert.Equal(1.5, catalog.Find("claude-sonnet-5-5")?.Input);
    }
}
