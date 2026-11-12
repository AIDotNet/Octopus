using System.Text;
using System.Text.Json;

namespace Octopus.FileSystem;

/// <summary>
/// Picks dotted paths out of one JSON line without building a document: a <see cref="Utf8JsonReader"/> walks the
/// line once, enters only the objects and arrays that lead to a wanted path, skips everything else, and remembers
/// where each wanted value starts and ends. Selected values are later copied byte for byte. The whole line is still
/// read to the end, so a line that is not valid JSON (or half written) is never mistaken for a row.
/// </summary>
internal sealed class JsonLineProjector
{
    private readonly Node _root = new("", null);
    private readonly int[] _selectSlots;
    private readonly WhereClause[] _where;
    private readonly Capture[] _captures;

    public JsonLineProjector(IReadOnlyList<string> select, IReadOnlyDictionary<string, JsonElement>? where)
    {
        var slots = 0;
        _selectSlots = new int[select.Count];
        for (var i = 0; i < select.Count; i++) _selectSlots[i] = SlotOf(select[i], ref slots);
        var clauses = new List<WhereClause>();
        foreach (var (path, value) in where ?? new Dictionary<string, JsonElement>())
        {
            var expected = value.Clone();
            clauses.Add(new WhereClause(SlotOf(path, ref slots), expected,
                expected.ValueKind == JsonValueKind.String ? Encoding.UTF8.GetBytes(expected.GetString()!) : null));
        }
        _where = [.. clauses];
        _captures = new Capture[slots];
    }

    /// <summary>
    /// Reads <paramref name="line"/>. False when it is not one valid JSON value. Otherwise <paramref name="matches"/>
    /// tells whether every where-clause holds; the selected values can then be written with <see cref="WriteRow"/>.
    /// </summary>
    public bool TryRead(ReadOnlySpan<byte> line, out bool matches)
    {
        matches = false;
        Array.Fill(_captures, new Capture(-1, 0, JsonTokenType.None));
        var reader = new Utf8JsonReader(line, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        try
        {
            if (!reader.Read()) return false;
            Visit(ref reader, _root);
            if (reader.Read()) return false; // a second value after the first
        }
        catch (JsonException)
        {
            return false;
        }
        foreach (var clause in _where)
        {
            var capture = _captures[clause.Slot];
            if (capture.Start < 0 || !Equal(line.Slice(capture.Start, capture.Length), capture.Type, clause)) return true;
        }
        matches = true;
        return true;
    }

    /// <summary>The bytes <see cref="WriteRow"/> will add for this line, give or take the separators.</summary>
    public int RowSize()
    {
        var size = 2 + _selectSlots.Length;
        foreach (var slot in _selectSlots) size += _captures[slot].Start < 0 ? 4 : _captures[slot].Length;
        return size;
    }

    /// <summary>Writes one row (an array with one value per selected path, null where missing) for the line last read.</summary>
    public void WriteRow(Utf8JsonWriter writer, ReadOnlySpan<byte> line)
    {
        writer.WriteStartArray();
        foreach (var slot in _selectSlots)
        {
            var capture = _captures[slot];
            if (capture.Start < 0) writer.WriteNullValue();
            else writer.WriteRawValue(line.Slice(capture.Start, capture.Length), skipInputValidation: true); // the reader validated it
        }
        writer.WriteEndArray();
    }

    /// <summary>Reads the value the reader is on, entering it only when a wanted path continues inside.</summary>
    private void Visit(ref Utf8JsonReader reader, Node node)
    {
        var start = (int)reader.TokenStartIndex;
        var type = reader.TokenType;
        if (node.Children.Count > 0 && type == JsonTokenType.StartObject)
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                Node? child = null;
                foreach (var candidate in node.Children)
                {
                    // Compares against the unescaped name, so "type" finds "type".
                    if (reader.ValueTextEquals(candidate.Utf8Name))
                    {
                        child = candidate;
                        break;
                    }
                }
                reader.Read();
                if (child is null) reader.Skip();
                else Visit(ref reader, child);
            }
        }
        else if (node.Children.Count > 0 && type == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                Node? child = null;
                foreach (var candidate in node.Children)
                {
                    if (candidate.Index == index)
                    {
                        child = candidate;
                        break;
                    }
                }
                if (child is null) reader.Skip();
                else Visit(ref reader, child);
                index++;
            }
        }
        else
        {
            reader.Skip(); // nothing for a single token; to the matching end for an object or array
        }
        // A repeated property overwrites the earlier capture: the last one wins, as in JSON.parse.
        if (node.Slot >= 0) _captures[node.Slot] = new Capture(start, (int)reader.BytesConsumed - start, type);
    }

    private static bool Equal(ReadOnlySpan<byte> raw, JsonTokenType type, WhereClause clause)
    {
        switch (clause.Expected.ValueKind)
        {
            case JsonValueKind.String:
                if (type != JsonTokenType.String) return false;
                var reader = new Utf8JsonReader(raw);
                reader.Read();
                return reader.ValueTextEquals(clause.Utf8String);
            case JsonValueKind.True:
                return type == JsonTokenType.True;
            case JsonValueKind.False:
                return type == JsonTokenType.False;
            case JsonValueKind.Null:
                return type == JsonTokenType.Null;
            default:
                // Numbers, objects and arrays: rare in filters, so a small document is fine here.
                using (var document = JsonDocument.Parse(raw.ToArray()))
                {
                    return JsonElement.DeepEquals(document.RootElement, clause.Expected);
                }
        }
    }

    private int SlotOf(string path, ref int slots)
    {
        var node = _root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = node.Children.Find(c => c.Name == segment);
            if (child is null)
            {
                child = new Node(segment, int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) ? index : null);
                node.Children.Add(child);
            }
            node = child;
        }
        if (node.Slot < 0) node.Slot = slots++;
        return node.Slot;
    }

    private sealed class Node(string name, int? index)
    {
        public string Name { get; } = name;
        public byte[] Utf8Name { get; } = Encoding.UTF8.GetBytes(name);
        /// <summary>The segment as an array index, when it is a non-negative integer.</summary>
        public int? Index { get; } = index;
        public List<Node> Children { get; } = [];
        /// <summary>Where this path's value is remembered; -1 when no select or where names this exact path.</summary>
        public int Slot { get; set; } = -1;
    }

    private readonly record struct Capture(int Start, int Length, JsonTokenType Type);

    private sealed record WhereClause(int Slot, JsonElement Expected, byte[]? Utf8String);
}
