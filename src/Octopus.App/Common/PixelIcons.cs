using System.Text.Json;
using Avalonia.Platform;

namespace Octopus.App.Common;

/// <summary>One filled rectangle of a pixel icon, in pixels of its 24 x 24 grid.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// A built-in pixel icon a project square can show: a 24 x 24 bitmap (see Assets/PixelIcons/README.md), kept as the
/// filled rectangles it is made of so it can be drawn at a whole multiple of its pixel size.
/// </summary>
public sealed class PixelIcon
{
    public const int Size = 24;

    private readonly uint[] _rows;

    internal PixelIcon(string id, string name, string category, string source, uint[] rows)
    {
        Id = id;
        Name = name;
        Category = category;
        Source = source;
        _rows = rows;
        Rects = Merge(rows);
    }

    /// <summary>Unique: the pixelarticons name, or "hn-" and the HackerNoon name.</summary>
    public string Id { get; }

    /// <summary>The English name the search matches ("t-rex").</summary>
    public string Name { get; }

    /// <summary>One of <see cref="PixelIcons.Categories"/>.</summary>
    public string Category { get; }

    /// <summary>"pixelarticons" or "hackernoon".</summary>
    public string Source { get; }

    public IReadOnlyList<PixelRect> Rects { get; }

    /// <summary>Whether the pixel at column <paramref name="x"/>, row <paramref name="y"/> is set.</summary>
    public bool this[int x, int y] => (_rows[y] >> (Size - 1 - x) & 1) != 0;

    /// <summary>The set pixels as rectangles: runs of a row, joined with the same run of the rows below.</summary>
    private static List<PixelRect> Merge(uint[] rows)
    {
        var done = new List<PixelRect>();
        var open = new Dictionary<(int Start, int End), PixelRect>();
        for (var y = 0; y <= Size; y++)
        {
            var runs = new HashSet<(int Start, int End)>();
            if (y < Size)
            {
                for (var x = 0; x < Size;)
                {
                    if ((rows[y] >> (Size - 1 - x) & 1) == 0)
                    {
                        x++;
                        continue;
                    }
                    var start = x;
                    while (x < Size && (rows[y] >> (Size - 1 - x) & 1) != 0)
                    {
                        x++;
                    }
                    runs.Add((start, x));
                }
            }
            foreach (var (run, rect) in open.ToList())
            {
                if (runs.Remove(run))
                {
                    open[run] = rect with { Height = rect.Height + 1 };
                }
                else
                {
                    done.Add(rect);
                    open.Remove(run);
                }
            }
            foreach (var run in runs)
            {
                open[run] = new PixelRect(run.Start, y, run.End - run.Start, 1);
            }
        }
        return done;
    }
}

/// <summary>
/// The built-in pixel icons, loaded once from <c>Assets/PixelIcons/pixel-icons.json</c>. A project stores one as
/// <c>pixel:&lt;id&gt;</c> in <see cref="Core.Projects.Project.Icon"/>, beside the Lucide names it already takes.
/// </summary>
public static class PixelIcons
{
    public const string Prefix = "pixel:";

    /// <summary>The categories, in the order the icon picker shows them.</summary>
    public static IReadOnlyList<string> Categories { get; } = ["fun", "nature", "life", "work", "dev", "brands", "letters", "other"];

    private static readonly Lazy<(IReadOnlyList<PixelIcon> All, Dictionary<string, PixelIcon> ById)> s_icons = new(Load);

    public static IReadOnlyList<PixelIcon> All => s_icons.Value.All;

    public static PixelIcon? Find(string? id) => id is not null && s_icons.Value.ById.TryGetValue(id, out var icon) ? icon : null;

    /// <summary>The pixel icon a stored value names ("pixel:t-rex"); null for anything else.</summary>
    public static PixelIcon? Parse(string? stored) =>
        stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal) ? Find(stored[Prefix.Length..]) : null;

    /// <summary>The value to store for the icon.</summary>
    public static string Reference(PixelIcon icon) => Prefix + icon.Id;

    private static (IReadOnlyList<PixelIcon>, Dictionary<string, PixelIcon>) Load()
    {
        var list = new List<PixelIcon>();
        var byId = new Dictionary<string, PixelIcon>(StringComparer.Ordinal);
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Octopus/Assets/PixelIcons/pixel-icons.json"));
            using var document = JsonDocument.Parse(stream);
            foreach (var entry in document.RootElement.GetProperty("icons").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString();
                var bits = Convert.FromBase64String(entry.GetProperty("bits").GetString() ?? "");
                if (string.IsNullOrEmpty(id) || bits.Length != PixelIcon.Size * 3 || byId.ContainsKey(id))
                {
                    continue;
                }
                var rows = new uint[PixelIcon.Size];
                for (var y = 0; y < PixelIcon.Size; y++)
                {
                    rows[y] = (uint)(bits[y * 3] << 16 | bits[y * 3 + 1] << 8 | bits[y * 3 + 2]);
                }
                var icon = new PixelIcon(
                    id,
                    entry.GetProperty("name").GetString() ?? id,
                    entry.GetProperty("category").GetString() ?? "other",
                    entry.GetProperty("source").GetString() ?? "",
                    rows);
                list.Add(icon);
                byId[id] = icon;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            // No icons rather than no app: the picker then offers the line icons only.
        }
        return (list, byId);
    }
}
