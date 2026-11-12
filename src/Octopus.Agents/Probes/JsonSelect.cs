using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Octopus.Agents.Probes;

/// <summary>
/// Keeps only the declared paths of a probe's result, so fields nobody asked for (the email in
/// <c>cursor-agent about</c>, an account id) never leave the host. Paths are dotted from the top-level object:
/// <c>rateLimits</c> keeps that member whole, <c>session.cost</c> keeps <c>{"session":{"cost":…}}</c>. A path that
/// does not exist is left out.
/// </summary>
public static class JsonSelect
{
    /// <summary>Compact JSON of the selection; the whole value when <paramref name="paths"/> is empty. Null when paths are given but the value is not an object.</summary>
    public static string? Apply(JsonElement root, IReadOnlyList<string> paths)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (paths.Count == 0)
            {
                root.WriteTo(writer);
            }
            else
            {
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                WriteSelected(writer, root, Build(paths));
            }
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class Node
    {
        public bool Whole { get; set; }
        public List<(string Name, Node Child)> Children { get; } = [];

        public Node Child(string name)
        {
            foreach (var (n, child) in Children)
            {
                if (n == name)
                {
                    return child;
                }
            }
            var node = new Node();
            Children.Add((name, node));
            return node;
        }
    }

    private static Node Build(IReadOnlyList<string> paths)
    {
        var root = new Node();
        foreach (var path in paths)
        {
            var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }
            var node = root;
            foreach (var segment in segments)
            {
                node = node.Child(segment);
            }
            node.Whole = true;
        }
        return root;
    }

    private static void WriteSelected(Utf8JsonWriter writer, JsonElement element, Node node)
    {
        writer.WriteStartObject();
        foreach (var (name, child) in node.Children)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }
            if (child.Whole)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName(name);
                WriteSelected(writer, value, child);
            }
        }
        writer.WriteEndObject();
    }
}
