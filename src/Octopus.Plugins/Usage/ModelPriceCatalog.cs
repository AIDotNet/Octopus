using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Octopus.Plugins.Usage;

/// <summary>Prices of one model in USD per million tokens; <see cref="Tier"/> applies past a prompt size.</summary>
public sealed record ModelPrice(double Input, double Output, double CacheRead, double CacheWrite, ModelPriceTier? Tier = null);

/// <summary>The prices of a model once a prompt (input and cache) is larger than <see cref="Size"/> tokens.</summary>
public sealed record ModelPriceTier(long Size, double Input, double Output, double CacheRead, double CacheWrite);

/// <summary>
/// Public model prices for cost estimates (design/plugins/12 §10), synced from models.dev, an open catalog of providers
/// and their models. The catalog is 6+ MB, far more than a plugin turn can parse, so the host fetches it at most once a
/// day and keeps an index of model id → prices (<c>model-prices.json</c>, beside the usage cache). A model is listed
/// under every provider that resells it, at that reseller's price: it is priced by its own vendor first (Anthropic for
/// Claude, OpenAI for GPT…), otherwise by the price most resellers agree on. Until the first sync a small built-in
/// table of Claude and GPT prices stands in.
/// </summary>
public sealed partial class ModelPriceCatalog : IDisposable
{
    public static readonly Uri Source = new("https://models.dev/api.json");
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// <summary>Providers that are a model's own vendor, in the order they win when several list the same id.</summary>
    private static readonly string[] Vendors =
        ["anthropic", "openai", "google", "xai", "deepseek", "xiaomi", "moonshotai", "zhipuai", "minimax", "mistral", "cohere", "alibaba"];

    /// <summary>The stand-in until the first sync: id pattern, input, output, cache read (checked 2026-11-15).</summary>
    private static readonly (Regex Pattern, double Input, double Output, double CacheRead)[] Builtin =
    [
        (new Regex(@"^claude-(fable|mythos)-5[-.]2"), 8, 40, 0.2),
        (new Regex(@"^claude-(fable|mythos)-5[-.]1"), 10, 50, 0.25),
        (new Regex(@"^claude-(fable|mythos)-5\b"), 10, 50, 1),
        (new Regex(@"^claude-opus-5[-.]5"), 4, 20, 0.2),
        (new Regex(@"^claude-opus-5\b"), 5, 25, 0.5),
        (new Regex(@"^claude-opus-4[-.][5-9]"), 5, 25, 0.5),
        (new Regex(@"^claude-opus-4\b"), 15, 75, 1.5),
        (new Regex(@"^claude-sonnet-5[-.]5"), 2, 10, 0.1),
        (new Regex(@"^claude-sonnet-5\b"), 2, 10, 0.2),
        (new Regex(@"^claude-sonnet-4"), 3, 15, 0.3),
        (new Regex(@"^claude-haiku-5[-.]5"), 0.1, 0.5, 0.01),
        (new Regex(@"^claude-haiku-4"), 1, 5, 0.1),
        (new Regex(@"^gpt-6[-.]1-sol"), 2, 10, 0.1),
    ];

    private readonly string _file;
    private readonly Func<CancellationToken, Task<Stream>> _download;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HttpClient? _client;
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, ModelPrice> _models = new Dictionary<string, ModelPrice>();
    private Task? _sync;

    /// <param name="download">Opens the catalog; the default asks <see cref="Source"/> over https.</param>
    public ModelPriceCatalog(PluginPaths paths, Func<CancellationToken, Task<Stream>>? download = null, Func<DateTimeOffset>? clock = null)
    {
        _file = Path.Combine(Path.GetDirectoryName(paths.PluginsDirectory)!, "model-prices.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (download is null)
        {
            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("Octopus");
            download = ct => _client.GetStreamAsync(Source, ct);
        }
        _download = download;
        Load();
    }

    /// <summary>When the index was last synced; null while only the built-in table is known.</summary>
    public DateTimeOffset? SyncedAt { get; private set; }

    /// <summary>Why the last sync failed; null after a good one.</summary>
    public string? LastError { get; private set; }

    public bool IsSyncing => _sync is not null;

    /// <summary>How many models the synced index prices.</summary>
    public int Count => _models.Count;

    /// <summary>Raised after a sync that changed a price.</summary>
    public event EventHandler? Changed;

    public void Dispose() => _client?.Dispose();

    /// <summary>
    /// Starts a sync when the index is missing or a day old (or always, with <paramref name="force"/>); the task ends
    /// when that sync does. Never throws: a failure is kept in <see cref="LastError"/> and the old index stays.
    /// </summary>
    public Task EnsureFreshAsync(bool force = false)
    {
        lock (_gate)
        {
            if (_sync is not null) return _sync;
            if (!force && SyncedAt is { } at && _clock() - at < MaxAge) return Task.CompletedTask;
            return _sync = Task.Run(SyncAsync);
        }
    }

    private async Task SyncAsync()
    {
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await using var stream = await _download(cancel.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancel.Token).ConfigureAwait(false);
            var models = BuildIndex(document.RootElement);
            if (models.Count == 0) throw new InvalidDataException("The catalog lists no priced models.");
            var changed = !SameAs(_models, models);
            _models = models;
            SyncedAt = _clock();
            LastError = null;
            Save();
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException or InvalidOperationException)
        {
            LastError = e is OperationCanceledException ? "timed out" : e.Message;
        }
        finally
        {
            lock (_gate) _sync = null;
        }
    }

    /// <summary>The prices of a model, or null when neither the synced index nor the built-in table knows it.</summary>
    public ModelPrice? Find(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var key = Normalize(model);
        var models = _models;
        if (models.Count > 0)
        {
            // Claude Code writes "claude-sonnet-5.5" as well as "claude-sonnet-5-5"; GPT ids keep their dots; some ids
            // carry a release date.
            foreach (var candidate in (string[])[key, DigitDot().Replace(key, "$1-$2"), DigitDash().Replace(key, "$1.$2"), DateSuffix().Replace(key, "")])
            {
                if (models.TryGetValue(candidate, out var price)) return price;
            }
        }
        foreach (var (pattern, input, output, cacheRead) in Builtin)
        {
            if (pattern.IsMatch(key)) return new ModelPrice(input, output, cacheRead, input * 1.25);
        }
        return null;
    }

    /// <summary>The catalog → { key: prices }, each model priced by its vendor, else by the price most resellers agree on.</summary>
    public static Dictionary<string, ModelPrice> BuildIndex(JsonElement catalog)
    {
        var vendor = new Dictionary<string, (int Rank, ModelPrice Price)>(StringComparer.Ordinal);
        var votes = new Dictionary<string, Dictionary<ModelPrice, int>>(StringComparer.Ordinal);
        if (catalog.ValueKind != JsonValueKind.Object) return [];
        foreach (var provider in catalog.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object || !provider.Value.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object) continue;
            var rank = Array.IndexOf(Vendors, provider.Name);
            foreach (var model in models.EnumerateObject())
            {
                if (model.Value.ValueKind != JsonValueKind.Object || !model.Value.TryGetProperty("cost", out var cost) || PriceOf(cost) is not { } price) continue;
                var key = Normalize(model.Name);
                if (rank >= 0)
                {
                    if (!vendor.TryGetValue(key, out var known) || rank < known.Rank) vendor[key] = (rank, price);
                }
                else if (price.Input > 0 || price.Output > 0)
                {
                    // A free listing (a coding plan, a promotion) says nothing about the model's price.
                    if (!votes.TryGetValue(key, out var tally)) votes[key] = tally = [];
                    tally[price] = tally.GetValueOrDefault(price) + 1;
                }
            }
        }
        var index = new Dictionary<string, ModelPrice>(StringComparer.Ordinal);
        foreach (var (key, tally) in votes)
        {
            // The most common price; a tie goes to the one listed first, so a sync is stable.
            index[key] = tally.MaxBy(pair => pair.Value).Key;
        }
        foreach (var (key, (_, price)) in vendor) index[key] = price;
        return index;
    }

    private static ModelPrice? PriceOf(JsonElement cost)
    {
        if (cost.ValueKind != JsonValueKind.Object || Number(cost, "input") is not { } input || Number(cost, "output") is not { } output) return null;
        ModelPriceTier? tier = null;
        // Above a context size some models cost more (Claude Haiku 5.5 past 100K, GPT-6.1 Sol past 272K).
        if (cost.TryGetProperty("tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tiers.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("tier", out var limit) || limit.ValueKind != JsonValueKind.Object) continue;
                if (limit.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "context"
                    && Number(limit, "size") is { } size && Number(t, "input") is { } tierInput)
                {
                    tier = new ModelPriceTier((long)size, tierInput, Number(t, "output") ?? output, Number(t, "cache_read") ?? tierInput, Number(t, "cache_write") ?? tierInput);
                    break;
                }
            }
        }
        return new ModelPrice(input, output, Number(cost, "cache_read") ?? input, Number(cost, "cache_write") ?? input, tier);
    }

    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 ? d : null;

    /// <summary>"anthropic/claude-opus-5-5[1m]" → "claude-opus-5-5": the id without its route, variant or leading marker.</summary>
    public static string Normalize(string id)
    {
        var key = id.Trim().ToLowerInvariant();
        var bracket = key.IndexOf('[');
        if (bracket > 0) key = key[..bracket];
        key = key[(key.LastIndexOf('/') + 1)..];
        return key.TrimStart('~');
    }

    private static bool SameAs(IReadOnlyDictionary<string, ModelPrice> a, IReadOnlyDictionary<string, ModelPrice> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && other == pair.Value);

    public static JsonObject ToJson(ModelPrice price)
    {
        var json = new JsonObject
        {
            ["input"] = price.Input, ["output"] = price.Output, ["cacheRead"] = price.CacheRead, ["cacheWrite"] = price.CacheWrite,
        };
        if (price.Tier is { } tier)
        {
            json["tier"] = new JsonObject
            {
                ["size"] = tier.Size, ["input"] = tier.Input, ["output"] = tier.Output, ["cacheRead"] = tier.CacheRead, ["cacheWrite"] = tier.CacheWrite,
            };
        }
        return json;
    }

    private static ModelPrice? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        static double D(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        var tier = o["tier"] is JsonObject t ? new ModelPriceTier((long)D(t, "size"), D(t, "input"), D(t, "output"), D(t, "cacheRead"), D(t, "cacheWrite")) : null;
        return new ModelPrice(D(o, "input"), D(o, "output"), D(o, "cacheRead"), D(o, "cacheWrite"), tier);
    }

    private void Save()
    {
        var models = new JsonObject();
        foreach (var (key, price) in _models.OrderBy(pair => pair.Key, StringComparer.Ordinal)) models[key] = ToJson(price);
        var document = new JsonObject { ["source"] = Source.ToString(), ["syncedAt"] = SyncedAt?.ToUnixTimeMilliseconds(), ["models"] = models };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = $"{_file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, document.ToJsonString());
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept in memory for this run; the next start syncs again.
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file) || JsonNode.Parse(File.ReadAllText(_file)) is not JsonObject document || document["models"] is not JsonObject models) return;
            var index = new Dictionary<string, ModelPrice>(StringComparer.Ordinal);
            foreach (var (key, value) in models)
            {
                if (FromJson(value) is { } price) index[key] = price;
            }
            if (index.Count == 0) return;
            _models = index;
            if (document["syncedAt"] is JsonValue at && at.TryGetValue<long>(out var ms)) SyncedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
        }
    }

    [GeneratedRegex(@"(\d)\.(\d)")]
    private static partial Regex DigitDot();

    [GeneratedRegex(@"(\d)-(\d)(?=$|-)")]
    private static partial Regex DigitDash();

    [GeneratedRegex(@"-\d{8}$")]
    private static partial Regex DateSuffix();
}
