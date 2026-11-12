using System.Globalization;
using System.Text;

namespace Octopus.Agents.Signals;

/// <summary>
/// Just enough TOML to find a user's own single-slot command (Codex's top-level <c>notify</c>, Grok's
/// <c>[ui.status_line]</c>): tables, dotted and quoted keys, the four string forms, arrays and inline tables. Every
/// value ends up in a flat map keyed by its full dotted path; inline tables are flattened the same way, so
/// <c>[ui.status_line] command = …</c> and <c>[ui] status_line = { command = … }</c> both give
/// <c>ui.status_line.command</c>. Numbers, booleans and dates are kept as their raw text. A line that cannot be read
/// is skipped rather than failing the file: a missed key only means no chained command.
/// </summary>
internal static class MiniToml
{
    /// <summary>A scalar other than a string, kept as written.</summary>
    public sealed record Raw(string Text);

    private sealed class TomlError(string message) : Exception(message);

    public static Dictionary<string, object?> Parse(string text)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var parser = new Parser(text);
        var table = "";
        var arrayTables = new Dictionary<string, int>(StringComparer.Ordinal);
        while (true)
        {
            parser.SkipBlank(newlines: true);
            if (parser.AtEnd)
            {
                break;
            }
            var lineStart = parser.Position;
            try
            {
                if (parser.Peek == '[')
                {
                    var array = parser.Peek2 == '[';
                    parser.Advance(array ? 2 : 1);
                    parser.SkipBlank(newlines: false);
                    var name = string.Join('.', parser.ReadKey());
                    parser.SkipBlank(newlines: false);
                    parser.Expect(array ? "]]" : "]");
                    if (array)
                    {
                        // Each [[name]] is a new element; keys under it must not merge with the previous one's.
                        var index = arrayTables.TryGetValue(name, out var n) ? n + 1 : 0;
                        arrayTables[name] = index;
                        table = $"{name}[{index.ToString(CultureInfo.InvariantCulture)}]";
                    }
                    else
                    {
                        table = name;
                    }
                }
                else
                {
                    var key = parser.ReadKey();
                    parser.SkipBlank(newlines: false);
                    parser.Expect("=");
                    parser.SkipBlank(newlines: false);
                    var value = parser.ReadValue();
                    Store(values, Join(table, string.Join('.', key)), value);
                }
                parser.SkipBlank(newlines: false);
                if (!parser.AtEnd && parser.Peek != '\n' && parser.Peek != '\r')
                {
                    throw new TomlError("unexpected text after value");
                }
            }
            catch (TomlError)
            {
                parser.Position = lineStart;
                parser.SkipLine();
            }
        }
        return values;
    }

    private static string Join(string table, string key) => table.Length == 0 ? key : table + "." + key;

    private static void Store(Dictionary<string, object?> values, string path, object? value)
    {
        if (value is Dictionary<string, object?> inline)
        {
            foreach (var (k, v) in inline)
            {
                Store(values, path + "." + k, v);
            }
            return;
        }
        values[path] = value;
    }

    private sealed class Parser(string text)
    {
        public int Position { get; set; }
        public bool AtEnd => Position >= text.Length;
        public char Peek => Position < text.Length ? text[Position] : '\0';
        public char Peek2 => Position + 1 < text.Length ? text[Position + 1] : '\0';

        public void Advance(int count) => Position = Math.Min(text.Length, Position + count);

        public void Expect(string token)
        {
            if (string.CompareOrdinal(text, Position, token, 0, token.Length) != 0)
            {
                throw new TomlError($"expected '{token}'");
            }
            Position += token.Length;
        }

        /// <summary>Spaces, tabs and comments; newlines too when asked.</summary>
        public void SkipBlank(bool newlines)
        {
            while (!AtEnd)
            {
                var c = Peek;
                if (c is ' ' or '\t' || (newlines && c is '\n' or '\r'))
                {
                    Position++;
                }
                else if (c == '#')
                {
                    while (!AtEnd && Peek != '\n')
                    {
                        Position++;
                    }
                }
                else
                {
                    return;
                }
            }
        }

        public void SkipLine()
        {
            while (!AtEnd && Peek != '\n')
            {
                Position++;
            }
        }

        public List<string> ReadKey()
        {
            var parts = new List<string>();
            while (true)
            {
                SkipBlank(newlines: false);
                if (Peek == '"')
                {
                    parts.Add(ReadBasicString());
                }
                else if (Peek == '\'')
                {
                    parts.Add(ReadLiteralString());
                }
                else
                {
                    var start = Position;
                    while (!AtEnd && (char.IsAsciiLetterOrDigit(Peek) || Peek is '_' or '-'))
                    {
                        Position++;
                    }
                    if (Position == start)
                    {
                        throw new TomlError("expected a key");
                    }
                    parts.Add(text[start..Position]);
                }
                SkipBlank(newlines: false);
                if (Peek != '.')
                {
                    return parts;
                }
                Position++;
            }
        }

        public object? ReadValue()
        {
            switch (Peek)
            {
                case '"':
                    return StartsWith("\"\"\"") ? ReadMultilineBasicString() : ReadBasicString();
                case '\'':
                    return StartsWith("'''") ? ReadMultilineLiteralString() : ReadLiteralString();
                case '[':
                    return ReadArray();
                case '{':
                    return ReadInlineTable();
                default:
                    var start = Position;
                    while (!AtEnd && Peek is not (' ' or '\t' or ',' or ']' or '}' or '#' or '\n' or '\r'))
                    {
                        Position++;
                    }
                    if (Position == start)
                    {
                        throw new TomlError("expected a value");
                    }
                    return new Raw(text[start..Position]);
            }
        }

        private bool StartsWith(string token) => string.CompareOrdinal(text, Position, token, 0, token.Length) == 0;

        private List<object?> ReadArray()
        {
            Expect("[");
            var items = new List<object?>();
            while (true)
            {
                SkipBlank(newlines: true);
                if (Peek == ']')
                {
                    Position++;
                    return items;
                }
                items.Add(ReadValue());
                SkipBlank(newlines: true);
                if (Peek == ',')
                {
                    Position++;
                    continue;
                }
                Expect("]");
                return items;
            }
        }

        private Dictionary<string, object?> ReadInlineTable()
        {
            Expect("{");
            var table = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                SkipBlank(newlines: true);
                if (Peek == '}')
                {
                    Position++;
                    return table;
                }
                var key = string.Join('.', ReadKey());
                SkipBlank(newlines: false);
                Expect("=");
                SkipBlank(newlines: false);
                Store(table, key, ReadValue());
                SkipBlank(newlines: true);
                if (Peek == ',')
                {
                    Position++;
                    continue;
                }
                Expect("}");
                return table;
            }
        }

        private string ReadLiteralString()
        {
            Expect("'");
            var end = text.IndexOf('\'', Position);
            var newline = text.IndexOf('\n', Position);
            if (end < 0 || (newline >= 0 && newline < end))
            {
                throw new TomlError("unterminated string");
            }
            var value = text[Position..end];
            Position = end + 1;
            return value;
        }

        private string ReadMultilineLiteralString()
        {
            Expect("'''");
            SkipFirstNewline();
            var end = text.IndexOf("'''", Position, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new TomlError("unterminated string");
            }
            // Up to two quotes may sit right before the closing delimiter.
            while (end + 3 < text.Length && text[end + 3] == '\'')
            {
                end++;
            }
            var value = text[Position..end];
            Position = end + 3;
            return value;
        }

        private string ReadBasicString()
        {
            Expect("\"");
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd || Peek == '\n')
                {
                    throw new TomlError("unterminated string");
                }
                var c = text[Position++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    ReadEscape(sb);
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        private string ReadMultilineBasicString()
        {
            Expect("\"\"\"");
            SkipFirstNewline();
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new TomlError("unterminated string");
                }
                if (StartsWith("\"\"\"") && !(Position + 3 < text.Length && text[Position + 3] == '"'))
                {
                    Position += 3;
                    return sb.ToString();
                }
                var c = text[Position++];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                // A backslash at the end of a line joins it with the next non-blank text.
                var look = Position;
                while (look < text.Length && text[look] is ' ' or '\t')
                {
                    look++;
                }
                if (look < text.Length && text[look] is '\n' or '\r')
                {
                    Position = look;
                    while (!AtEnd && Peek is ' ' or '\t' or '\n' or '\r')
                    {
                        Position++;
                    }
                    continue;
                }
                ReadEscape(sb);
            }
        }

        private void SkipFirstNewline()
        {
            if (Peek == '\r' && Peek2 == '\n')
            {
                Position += 2;
            }
            else if (Peek == '\n')
            {
                Position++;
            }
        }

        private void ReadEscape(StringBuilder sb)
        {
            if (AtEnd)
            {
                throw new TomlError("bad escape");
            }
            var e = text[Position++];
            switch (e)
            {
                case 'b': sb.Append('\b'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'f': sb.Append('\f'); break;
                case 'r': sb.Append('\r'); break;
                case 'e': sb.Append('\u001b'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u': sb.Append(ReadCodePoint(4)); break;
                case 'U': sb.Append(ReadCodePoint(8)); break;
                default: throw new TomlError("bad escape");
            }
        }

        private string ReadCodePoint(int digits)
        {
            if (Position + digits > text.Length
                || !int.TryParse(text.AsSpan(Position, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
                || code is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
            {
                throw new TomlError("bad unicode escape");
            }
            Position += digits;
            return char.ConvertFromUtf32(code);
        }
    }
}
