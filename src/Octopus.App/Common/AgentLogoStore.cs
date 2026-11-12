using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace Octopus.App.Common;

/// <summary>
/// Loads the agent logos from the <c>Assets/AgentLogos/&lt;id&gt;.json</c> resources, once per id. A missing or broken
/// logo is null (the icon then falls back to its letter tile), never an exception.
/// </summary>
internal sealed partial class AgentLogoStore
{
    public static AgentLogoStore Instance { get; } = new();

    private readonly Dictionary<string, AgentLogo?> _cache = new(StringComparer.Ordinal);

    public AgentLogo? Get(string? id)
    {
        if (string.IsNullOrEmpty(id) || !IsSafeKey(id))
        {
            return null;
        }

        lock (_cache)
        {
            if (!_cache.TryGetValue(id, out var logo))
            {
                logo = Load(id);
                _cache[id] = logo;
            }

            return logo;
        }
    }

    private static bool IsSafeKey(string id)
    {
        foreach (var c in id)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static AgentLogo? Load(string id)
    {
        try
        {
            var uri = new Uri($"avares://Octopus/Assets/AgentLogos/{id}.json");
            if (!AssetLoader.Exists(uri))
            {
                return null;
            }

            using var stream = AssetLoader.Open(uri);
            var raw = JsonSerializer.Deserialize(stream, LogoJsonContext.Default.RawLogo);
            return raw is null ? null : Convert(raw);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AgentLogo? Convert(RawLogo raw)
    {
        if (raw.ViewBox is not { Length: 4 } vb || vb[2] <= 0 || vb[3] <= 0 || raw.Paths is not { Count: > 0 })
        {
            return null;
        }

        var shapes = new List<AgentLogoShape>(raw.Paths.Count);
        foreach (var p in raw.Paths)
        {
            if (string.IsNullOrWhiteSpace(p.D))
            {
                continue;
            }

            // Avalonia's path mini-language carries the fill rule as a prefix: F0 = even-odd, F1 = non-zero.
            var rule = string.Equals(p.FillRule, "evenodd", StringComparison.OrdinalIgnoreCase) ? "F0 " : "F1 ";
            var geometry = StreamGeometry.Parse(rule + p.D);
            IBrush? fill = null;
            if (!string.Equals(p.Fill, "currentColor", StringComparison.OrdinalIgnoreCase))
            {
                if (p.Fill is null || !Color.TryParse(p.Fill, out var color))
                {
                    continue;
                }

                fill = new ImmutableSolidColorBrush(color);
            }

            shapes.Add(new AgentLogoShape(geometry, fill));
        }

        return shapes.Count == 0 ? null : new AgentLogo(new Rect(vb[0], vb[1], vb[2], vb[3]), shapes);
    }

    internal sealed class RawLogo
    {
        [JsonPropertyName("viewBox")] public double[]? ViewBox { get; set; }
        [JsonPropertyName("paths")] public List<RawPath>? Paths { get; set; }
    }

    internal sealed class RawPath
    {
        [JsonPropertyName("d")] public string? D { get; set; }
        [JsonPropertyName("fill")] public string? Fill { get; set; }
        [JsonPropertyName("fillRule")] public string? FillRule { get; set; }
    }

    [JsonSerializable(typeof(RawLogo))]
    internal sealed partial class LogoJsonContext : JsonSerializerContext;
}
