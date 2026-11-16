using System.Text.Json.Nodes;
using Octopus.Plugins.Usage;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary>
/// <c>agents.modelPrices(models)</c>: public model prices for cost estimates, from the host's models.dev index
/// (<see cref="ModelPriceCatalog"/>). No permission: the prices are public and nothing of the plugin leaves the
/// machine; the host alone talks to models.dev, at most once a day.
/// </summary>
public sealed class ModelPricesModule(ModelPriceCatalog catalog) : IPluginApiModule
{
    public const int MaxModels = 500;

    /// <summary>How long a request waits for the very first sync before answering from the built-in table.</summary>
    public static readonly TimeSpan FirstSyncWait = TimeSpan.FromSeconds(20);

    public void Register(PluginApiRouter router) => router.Request("usage.prices", async (call, cancellationToken) =>
    {
        var refresh = call.Bool("refresh");
        var sync = catalog.EnsureFreshAsync(refresh);
        // A refresh is answered with what it brought; so is the first use, so that a plugin does not price its
        // records with the stand-in table and then again with the catalog moments later.
        if (refresh || catalog.SyncedAt is null)
        {
            try { await sync.WaitAsync(FirstSyncWait, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        var prices = new JsonObject();
        foreach (var model in (call["models"] as JsonArray ?? []).Take(MaxModels))
        {
            if (model is not JsonValue v || !v.TryGetValue<string>(out var id) || prices.ContainsKey(id)) continue;
            prices[id] = catalog.Find(id) is { } price ? ModelPriceCatalog.ToJson(price) : null;
        }
        return PluginReply.Of(new JsonObject
        {
            ["source"] = catalog.SyncedAt is null ? "builtin" : "models.dev",
            ["syncedAt"] = catalog.SyncedAt?.ToUnixTimeMilliseconds(),
            ["count"] = catalog.Count,
            ["syncing"] = catalog.IsSyncing,
            ["error"] = catalog.LastError,
            ["prices"] = prices,
        });
    });
}
