using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace Octopus.App.Common;

internal sealed record FileIconShape(
    Geometry Geometry,
    IBrush? Fill,
    bool FillUsesCurrentColor,
    IPen? Stroke,
    bool StrokeUsesCurrentColor,
    double StrokeWidth,
    double Opacity,
    Matrix? Transform);

internal sealed class FileIconGraphic(Rect viewBox, IReadOnlyList<FileIconShape> shapes)
{
    public Rect ViewBox { get; } = viewBox;
    public IReadOnlyList<FileIconShape> Shapes { get; } = shapes;
}

/// <summary>
/// Loads the built-in VS Code style colored file icon bundle (<c>Assets/FileIcons/file-icons.json</c>) once and
/// parses each icon's <see cref="StreamGeometry"/> lazily on first use. NativeAOT-safe via source-generated JSON.
/// </summary>
internal sealed partial class FileIconStore
{
    public static FileIconStore Instance { get; } = new();

    private readonly string _defaultFile = "file";
    private readonly string _defaultFolder = "folder";
    private readonly string _defaultFolderOpen = "folder-open";
    private readonly Dictionary<string, string> _fileNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fileExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _folderNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lightVariants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RawIcon> _rawIcons = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FileIconGraphic?> _graphics = new(StringComparer.Ordinal);

    private FileIconStore()
    {
        try
        {
            var uri = new Uri("avares://Octopus/Assets/FileIcons/file-icons.json");
            if (!AssetLoader.Exists(uri)) return;
            using var stream = AssetLoader.Open(uri);
            var bundle = JsonSerializer.Deserialize(stream, FileIconJsonContext.Default.RawBundle);
            if (bundle is null) return;

            if (!string.IsNullOrEmpty(bundle.DefaultFile)) _defaultFile = bundle.DefaultFile;
            if (!string.IsNullOrEmpty(bundle.DefaultFolder)) _defaultFolder = bundle.DefaultFolder;
            if (!string.IsNullOrEmpty(bundle.DefaultFolderOpen)) _defaultFolderOpen = bundle.DefaultFolderOpen;

            if (bundle.FileNames is not null)
            {
                foreach (var (k, v) in bundle.FileNames) _fileNames[k] = v;
            }
            if (bundle.FileExtensions is not null)
            {
                foreach (var (k, v) in bundle.FileExtensions) _fileExtensions[k] = v;
            }
            if (bundle.FolderNames is not null)
            {
                foreach (var (k, v) in bundle.FolderNames) _folderNames[k] = v;
            }
            if (bundle.LightVariants is not null)
            {
                foreach (var (k, v) in bundle.LightVariants) _lightVariants[k] = v;
            }
            if (bundle.Icons is not null)
            {
                foreach (var (k, v) in bundle.Icons) _rawIcons[k] = v;
            }
        }
        catch (Exception)
        {
            // Missing or broken resource falls back cleanly without crashing the app.
        }
    }

    internal IEnumerable<string> AllIconIds => _rawIcons.Keys;

    /// <summary>
    /// Resolves the icon key for a file or folder name using VS Code's 3-tier order:
    /// exact filename → longest compound extension → single extension → fallback.
    /// </summary>
    public string ResolveKey(string name, bool isDirectory, bool isExpanded = false)
    {
        if (string.IsNullOrEmpty(name))
        {
            return isDirectory ? (isExpanded ? _defaultFolderOpen : _defaultFolder) : _defaultFile;
        }

        if (isDirectory)
        {
            var trimmed = name.TrimEnd('/', '\\');
            if (_folderNames.TryGetValue(trimmed, out var folderIcon))
            {
                if (isExpanded)
                {
                    var openKey = folderIcon + "-open";
                    if (_rawIcons.ContainsKey(openKey)) return openKey;
                }
                return folderIcon;
            }
            return isExpanded ? _defaultFolderOpen : _defaultFolder;
        }

        if (_fileNames.TryGetValue(name, out var exact))
        {
            return exact;
        }

        // Try compound extensions from longest to shortest (e.g., "app.test.tsx" -> "test.tsx" -> "tsx").
        var dot = name.IndexOf('.');
        while (dot >= 0 && dot < name.Length - 1)
        {
            var ext = name[(dot + 1)..];
            if (_fileExtensions.TryGetValue(ext, out var matched))
            {
                return matched;
            }
            dot = name.IndexOf('.', dot + 1);
        }

        return _defaultFile;
    }

    public FileIconGraphic? Get(string? key, bool isLight = false)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var resolved = isLight && _lightVariants.TryGetValue(key, out var lightKey) ? lightKey : key;
        return _graphics.GetOrAdd(resolved, ParseGraphic);
    }

    private FileIconGraphic? ParseGraphic(string id)
    {
        if (!_rawIcons.TryGetValue(id, out var raw)
            || raw.ViewBox is not { Length: 4 } vb
            || vb[2] <= 0 || vb[3] <= 0
            || raw.Paths is not { Count: > 0 })
        {
            return null;
        }

        var shapes = new List<FileIconShape>(raw.Paths.Count);
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
            if (!string.IsNullOrEmpty(p.Fill))
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

            shapes.Add(new FileIconShape(
                geometry,
                fill,
                fillCurrent,
                stroke,
                strokeCurrent,
                strokeWidth,
                p.Opacity ?? 1.0,
                transform));
        }

        return shapes.Count == 0 ? null : new FileIconGraphic(new Rect(vb[0], vb[1], vb[2], vb[3]), shapes);
    }

    internal sealed class RawBundle
    {
        [JsonPropertyName("defaultFile")] public string? DefaultFile { get; set; }
        [JsonPropertyName("defaultFolder")] public string? DefaultFolder { get; set; }
        [JsonPropertyName("defaultFolderOpen")] public string? DefaultFolderOpen { get; set; }
        [JsonPropertyName("fileNames")] public Dictionary<string, string>? FileNames { get; set; }
        [JsonPropertyName("fileExtensions")] public Dictionary<string, string>? FileExtensions { get; set; }
        [JsonPropertyName("folderNames")] public Dictionary<string, string>? FolderNames { get; set; }
        [JsonPropertyName("lightVariants")] public Dictionary<string, string>? LightVariants { get; set; }
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
        [JsonPropertyName("s")] public string? Stroke { get; set; }
        [JsonPropertyName("sw")] public double? StrokeWidth { get; set; }
        [JsonPropertyName("eo")] public bool EvenOdd { get; set; }
        [JsonPropertyName("o")] public double? Opacity { get; set; }
        [JsonPropertyName("m")] public double[]? Matrix { get; set; }
    }

    [JsonSerializable(typeof(RawBundle))]
    internal sealed partial class FileIconJsonContext : JsonSerializerContext;
}
