using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace Octopus.App.Common;

internal sealed record ModelIconShape(
    Geometry Geometry,
    IBrush? Fill,
    bool FillUsesCurrentColor,
    IPen? Stroke,
    bool StrokeUsesCurrentColor,
    double StrokeWidth,
    double Opacity,
    Matrix? Transform);

internal sealed class ModelIconGraphic(Rect viewBox, IReadOnlyList<ModelIconShape> shapes)
{
    public Rect ViewBox { get; } = viewBox;
    public IReadOnlyList<ModelIconShape> Shapes { get; } = shapes;
}

/// <summary>
/// Loads the built-in LobeHub AI model/provider icon bundle (<c>Assets/ModelIcons/model-icons.json</c>) once and
/// parses each icon's <see cref="StreamGeometry"/> and gradient brushes lazily on first use.
/// NativeAOT-safe via source-generated JSON.
/// </summary>
internal sealed partial class ModelIconStore
{
    public static ModelIconStore Instance { get; } = new();

    private readonly Dictionary<string, RawIcon> _rawIcons = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ModelIconGraphic?> _graphics = new(StringComparer.Ordinal);

    private ModelIconStore()
    {
        try
        {
            var uri = new Uri("avares://Octopus/Assets/ModelIcons/model-icons.json");
            if (!AssetLoader.Exists(uri)) return;
            using var stream = AssetLoader.Open(uri);
            var bundle = JsonSerializer.Deserialize(stream, ModelIconJsonContext.Default.RawBundle);
            if (bundle?.Icons is null) return;
            foreach (var (k, v) in bundle.Icons)
            {
                _rawIcons[k] = v;
            }
        }
        catch (Exception)
        {
            // Missing or broken resource falls back cleanly to the default sparkle glyph.
        }
    }

    internal IEnumerable<string> AllIconIds => _rawIcons.Keys;

    public bool Contains(string? key) => !string.IsNullOrEmpty(key) && _rawIcons.ContainsKey(key);

    public ModelIconGraphic? Get(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return _graphics.GetOrAdd(key, ParseGraphic);
    }

    private ModelIconGraphic? ParseGraphic(string id) =>
        _rawIcons.TryGetValue(id, out var raw) ? ParseRawGraphic(raw) : null;

    internal static ModelIconGraphic? ParseRawGraphic(RawIcon raw)
    {
        if (raw.ViewBox is not { Length: 4 } vb
            || vb[2] <= 0 || vb[3] <= 0
            || raw.Paths is not { Count: > 0 })
        {
            return null;
        }

        var shapes = new List<ModelIconShape>(raw.Paths.Count);
        foreach (var p in raw.Paths)
        {
            if (string.IsNullOrWhiteSpace(p.D)) continue;

            Geometry geometry;
            try
            {
                var prefix = p.EvenOdd ? "F0 " : "F1 ";
                geometry = StreamGeometry.Parse(prefix + p.D);
            }
            catch (Exception)
            {
                continue;
            }

            IBrush? fill = null;
            var fillCurrent = false;
            if (p.Gradient is not null)
            {
                fill = ParseGradient(p.Gradient);
            }
            else if (!string.IsNullOrEmpty(p.Fill))
            {
                if (string.Equals(p.Fill, "currentColor", StringComparison.OrdinalIgnoreCase))
                {
                    fillCurrent = true;
                }
                else if (Color.TryParse(p.Fill, out var color))
                {
                    fill = new ImmutableSolidColorBrush(color);
                }
            }

            IPen? stroke = null;
            var strokeCurrent = false;
            var strokeWidth = p.StrokeWidth ?? 1.0;
            if (!string.IsNullOrEmpty(p.Stroke))
            {
                if (string.Equals(p.Stroke, "currentColor", StringComparison.OrdinalIgnoreCase))
                {
                    strokeCurrent = true;
                }
                else if (Color.TryParse(p.Stroke, out var strokeColor))
                {
                    stroke = new ImmutablePen(new ImmutableSolidColorBrush(strokeColor), strokeWidth);
                }
            }

            if (fill is null && !fillCurrent && stroke is null && !strokeCurrent) continue;

            Matrix? transform = null;
            if (p.Matrix is { Length: 6 } m)
            {
                // SVG matrix(a, b, c, d, e, f) maps to Avalonia Matrix(m11=a, m12=b, m21=c, m22=d, m31=e, m32=f).
                transform = new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]);
            }

            shapes.Add(new ModelIconShape(
                geometry,
                fill,
                fillCurrent,
                stroke,
                strokeCurrent,
                strokeWidth,
                p.Opacity ?? 1.0,
                transform));
        }

        return shapes.Count == 0 ? null : new ModelIconGraphic(new Rect(vb[0], vb[1], vb[2], vb[3]), shapes);
    }

    private static IBrush? ParseGradient(RawGradient raw)
    {
        if (raw.Stops is not { Count: > 0 } || raw.Points is null) return null;

        var stops = new GradientStops();
        foreach (var s in raw.Stops)
        {
            if (string.IsNullOrEmpty(s.Color) || !Color.TryParse(s.Color, out var baseColor)) continue;
            var alpha = s.Opacity is { } op
                ? (byte)Math.Clamp(Math.Round(op * baseColor.A), 0, 255)
                : baseColor.A;
            stops.Add(new GradientStop(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B), s.Offset));
        }

        if (stops.Count == 0) return null;
        var unit = raw.UserSpace ? RelativeUnit.Absolute : RelativeUnit.Relative;

        if (string.Equals(raw.Kind, "r", StringComparison.OrdinalIgnoreCase) && raw.Points.Length >= 5)
        {
            var radius = new RelativeScalar(Math.Max(0.001, raw.Points[2]), unit);
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(raw.Points[0], raw.Points[1], unit),
                GradientOrigin = new RelativePoint(raw.Points[3], raw.Points[4], unit),
                RadiusX = radius,
                RadiusY = radius,
                GradientStops = stops,
            };
            return brush.ToImmutable();
        }

        if (raw.Points.Length >= 4)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(raw.Points[0], raw.Points[1], unit),
                EndPoint = new RelativePoint(raw.Points[2], raw.Points[3], unit),
                GradientStops = stops,
            };
            return brush.ToImmutable();
        }

        return null;
    }

    internal sealed class RawBundle
    {
        [JsonPropertyName("icons")] public Dictionary<string, RawIcon>? Icons { get; set; }
    }

    internal sealed class RawIcon
    {
        [JsonPropertyName("vb")] public double[]? ViewBox { get; set; }
        [JsonPropertyName("p")] public List<RawPath>? Paths { get; set; }
    }

    internal sealed class RawPath
    {
        [JsonPropertyName("d")] public string? D { get; set; }
        [JsonPropertyName("f")] public string? Fill { get; set; }
        [JsonPropertyName("g")] public RawGradient? Gradient { get; set; }
        [JsonPropertyName("s")] public string? Stroke { get; set; }
        [JsonPropertyName("sw")] public double? StrokeWidth { get; set; }
        [JsonPropertyName("eo")] public bool EvenOdd { get; set; }
        [JsonPropertyName("o")] public double? Opacity { get; set; }
        [JsonPropertyName("m")] public double[]? Matrix { get; set; }
    }

    internal sealed class RawGradient
    {
        [JsonPropertyName("k")] public string? Kind { get; set; }
        [JsonPropertyName("u")] public bool UserSpace { get; set; }
        [JsonPropertyName("pts")] public double[]? Points { get; set; }
        [JsonPropertyName("st")] public List<RawStop>? Stops { get; set; }
    }

    internal sealed class RawStop
    {
        [JsonPropertyName("t")] public double Offset { get; set; }
        [JsonPropertyName("c")] public string? Color { get; set; }
        [JsonPropertyName("o")] public double? Opacity { get; set; }
    }

    [JsonSerializable(typeof(RawBundle))]
    internal sealed partial class ModelIconJsonContext : JsonSerializerContext;
}
