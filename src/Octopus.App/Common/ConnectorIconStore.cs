using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Platform;

namespace Octopus.App.Common;

/// <summary>
/// Loads the built-in Bot connector product logos (<c>Assets/ConnectorIcons/connector-icons.json</c>) once and
/// parses each logo's <see cref="Avalonia.Media.StreamGeometry"/> and gradient brushes lazily on first use.
/// NativeAOT-safe via <see cref="ModelIconStore.ModelIconJsonContext"/>.
/// </summary>
internal sealed class ConnectorIconStore
{
    public static ConnectorIconStore Instance { get; } = new();

    private readonly Dictionary<string, ModelIconStore.RawIcon> _rawIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ModelIconGraphic?> _graphics = new(StringComparer.OrdinalIgnoreCase);

    private ConnectorIconStore()
    {
        try
        {
            var uri = new Uri("avares://Octopus/Assets/ConnectorIcons/connector-icons.json");
            if (!AssetLoader.Exists(uri)) return;
            using var stream = AssetLoader.Open(uri);
            var bundle = JsonSerializer.Deserialize(stream, ModelIconStore.ModelIconJsonContext.Default.RawBundle);
            if (bundle?.Icons is null) return;
            foreach (var (k, v) in bundle.Icons)
            {
                _rawIcons[k] = v;
            }
        }
        catch (Exception)
        {
            // Missing or broken resource falls back cleanly to the letter badge.
        }
    }

    internal IEnumerable<string> AllIconIds => _rawIcons.Keys;

    public bool Contains(string? appId) => !string.IsNullOrEmpty(appId) && _rawIcons.ContainsKey(appId);

    public ModelIconGraphic? Get(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        return _graphics.GetOrAdd(appId.Trim(), id =>
            _rawIcons.TryGetValue(id, out var raw) ? ModelIconStore.ParseRawGraphic(raw) : null);
    }
}
