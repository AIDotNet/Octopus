using System.Collections.Frozen;
using System.Collections.Immutable;
using StreamMark.Core.Highlighting;
using StreamMark.Highlighting.TextMate;

namespace Octopus.App.Files;

/// <summary>The syntax roles of Arc's code palette (the roles of <c>TKCode.Arc.CodeBlockSyntaxKind</c>, which is internal).</summary>
public enum SyntaxRole : byte
{
    None,
    Comment,
    String,
    Number,
    Keyword,
    Type,
    Function,
    Property,
    Tag,
    Punctuation,
}

/// <summary>A colored run of one line (offsets relative to the line start).</summary>
public readonly record struct SyntaxSpan(int Start, int Length, SyntaxRole Role);

/// <summary>
/// Tokens from the same engine <c>arc:CodeBlock</c> uses: StreamMark's TextMate highlighter (public, a dependency of
/// Arc), in a Light+ session. Arc's own wrapper over it, <c>TKCode.Arc.CodeBlockHighlighter</c>, is internal; what it adds
/// is the table that reads a Light+ token color back as one of Arc's nine roles. That table is mirrored below until Arc
/// makes the highlighter public (see the report in design notes); the colors themselves come from Arc's public
/// <c>CodeBlock</c> design tokens, so they follow the theme.
/// </summary>
public static class CodeSyntax
{
    private enum Family { Code, Markup, Json, Yaml, Css }

    // Light+ foreground colors (0xAARRGGBB) as TextMateSharp reports them for VS Code's Light+ theme.
    private static readonly FrozenDictionary<uint, SyntaxRole> s_roles = new Dictionary<uint, SyntaxRole>
    {
        [0xFF008000] = SyntaxRole.Comment,
        [0xFFA31515] = SyntaxRole.String,
        [0xFF811F3F] = SyntaxRole.String,
        [0xFFD16969] = SyntaxRole.String,
        [0xFFEE0000] = SyntaxRole.String,
        [0xFF098658] = SyntaxRole.Number,
        [0xFF0000FF] = SyntaxRole.Keyword,
        [0xFFAF00DB] = SyntaxRole.Keyword,
        [0xFF267F99] = SyntaxRole.Type,
        [0xFF795E26] = SyntaxRole.Function,
        [0xFFFF0000] = SyntaxRole.Property,
        [0xFF800000] = SyntaxRole.Tag,
    }.ToFrozenDictionary();

    private const string PunctuationCharacters = "{}[]();,.<>:=+*/!?|&-";

    private static readonly Lazy<TextMateSyntaxHighlighter> s_engine = new(() => new TextMateSyntaxHighlighter());

    /// <summary>Whether TextMate has a grammar for the language identifier.</summary>
    public static bool Supports(string? language) => !string.IsNullOrWhiteSpace(language) && s_engine.Value.SupportsLanguage(language);

    /// <summary>An incremental session (one thread at a time), or null for a language without a grammar.</summary>
    public static IHighlightSession? CreateSession(string? language) =>
        Supports(language) ? s_engine.Value.CreateSession(language!, HighlightThemeKind.Light) : null;

    private static Family FamilyOf(string? language) => language switch
    {
        "xml" or "html" or "axaml" or "vue" or "svelte" or "astro" => Family.Markup,
        "json" => Family.Json,
        "yaml" => Family.Yaml,
        "css" or "scss" or "less" => Family.Css,
        _ => Family.Code,
    };

    private static SyntaxRole RoleOf(uint foreground, Family family)
    {
        if (foreground == 0) return SyntaxRole.None;
        switch (family)
        {
            case Family.Markup when foreground == 0xFF0000FF:
                return SyntaxRole.String;
            case Family.Json when foreground is 0xFF0451A5 or 0xFF267F99:
                return SyntaxRole.Property;
            case Family.Yaml when foreground == 0xFF800000:
                return SyntaxRole.Property;
            case Family.Yaml when foreground == 0xFF0000FF:
                return SyntaxRole.String;
            case Family.Css when foreground == 0xFF267F99:
                return SyntaxRole.Property;
        }
        return s_roles.GetValueOrDefault(foreground);
    }

    /// <summary>The role spans of one line (ascending, merged); plain stretches are not listed, except punctuation.</summary>
    public static SyntaxSpan[] SpansOf(string? language, ImmutableArray<HighlightToken> tokens, ReadOnlySpan<char> line)
    {
        var family = FamilyOf(language);
        var result = new List<SyntaxSpan>();
        var position = 0;
        if (!tokens.IsDefault)
        {
            foreach (var token in tokens)
            {
                var start = Math.Min(token.Start, line.Length);
                if (start >= line.Length) break;
                var end = Math.Min(line.Length, token.Start + token.Length);
                if (start > position) Emit(result, line, position, start, SyntaxRole.None);
                if (end > start && start >= position)
                {
                    Emit(result, line, start, end, RoleOf(token.Foreground, family));
                    position = end;
                }
            }
        }
        if (line.Length > position) Emit(result, line, position, line.Length, SyntaxRole.None);
        return result.Count == 0 ? [] : result.ToArray();
    }

    private static void Emit(List<SyntaxSpan> output, ReadOnlySpan<char> line, int start, int end, SyntaxRole role)
    {
        if (role != SyntaxRole.None)
        {
            Add(output, start, end - start, role);
            return;
        }
        // Operators and brackets keep the editor foreground in TextMate themes: Arc's punctuation role is restored by character.
        for (var i = start; i < end; i++)
        {
            if (PunctuationCharacters.Contains(line[i]))
            {
                Add(output, i, 1, SyntaxRole.Punctuation);
            }
        }
    }

    private static void Add(List<SyntaxSpan> output, int start, int length, SyntaxRole role)
    {
        if (output.Count > 0 && output[^1] is { } last && last.Role == role && last.Start + last.Length == start)
        {
            output[^1] = last with { Length = last.Length + length };
            return;
        }
        output.Add(new SyntaxSpan(start, length, role));
    }
}
