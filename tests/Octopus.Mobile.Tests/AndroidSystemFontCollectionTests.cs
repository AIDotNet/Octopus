using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// Regression tests for <see cref="AndroidSystemFontCollection"/> reproducing the Avalonia 12.2
/// <c>SystemFontCollection</c> bugs on Android 15 where <c>TryMatchCharacter</c> followed by
/// <see cref="Typeface.GlyphTypeface"/> threw <see cref="InvalidOperationException"/> for unnamed fallback fonts.
/// </summary>
public class AndroidSystemFontCollectionTests
{
    private static GlyphTypeface LoadRealGlyphTypeface(FontWeight weight = FontWeight.Normal)
    {
        var typeface = new Typeface(new FontFamily("fonts:GeistMono#Geist Mono"), FontStyle.Normal, weight);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyph));
        return glyph;
    }

    [AvaloniaFact]
    public void Unnamed_fallback_font_cached_at_Medium_still_resolves_at_Normal_instead_of_failing()
    {
        var mediumFace = LoadRealGlyphTypeface(FontWeight.Medium);
        var inner = new FakeSystemFontCollection();
        // Simulates Android 15's variable NotoSansCJK-Regular.ttc: TryMatchCharacter returns
        // "fonts:SystemFonts#Noto Sans CJK SC" at Normal, TryGetGlyphTypeface misses (unnamed in fonts.xml),
        // while TryGetNearestMatch finds the cached Medium face under the exact family name.
        inner.SeedNearest("Noto Sans CJK SC", mediumFace);
        inner.MatchResults[('⎿', FontStyle.Normal, FontWeight.Normal)] =
            new Typeface(new FontFamily("fonts:SystemFonts#Noto Sans CJK SC"), FontStyle.Normal, FontWeight.Normal);

        var wrapped = new AndroidSystemFontCollection(inner);

        Assert.True(wrapped.TryMatchCharacter('⎿', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, CultureInfo.InvariantCulture, out var matched));
        Assert.True(wrapped.TryGetGlyphTypeface(matched.FontFamily.Name, matched.Style, matched.Weight, matched.Stretch, out var resolved));
        Assert.Same(mediumFace, resolved);
    }

    [AvaloniaTheory]
    [InlineData("Noto Sans Old Italic")]
    [InlineData("Noto Serif Hentaigana ExtraLight")]
    public void Unnamed_fallback_font_with_style_or_weight_keyword_in_name_resolves_via_unnormalized_nearest_match(string familyName)
    {
        var face = LoadRealGlyphTypeface();
        var inner = new FakeSystemFontCollection();
        inner.SeedNearest(familyName, face);
        inner.MatchResults[('A', FontStyle.Normal, FontWeight.Normal)] =
            new Typeface(new FontFamily($"fonts:SystemFonts#{familyName}"), FontStyle.Normal, FontWeight.Normal);
        inner.MatchResults[('A', FontStyle.Normal, FontWeight.Bold)] =
            new Typeface(new FontFamily($"fonts:SystemFonts#{familyName}"), FontStyle.Normal, FontWeight.Bold);

        var wrapped = new AndroidSystemFontCollection(inner);

        Assert.True(wrapped.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, CultureInfo.InvariantCulture, out var regular));
        Assert.True(wrapped.TryGetGlyphTypeface(regular.FontFamily.Name, regular.Style, regular.Weight, regular.Stretch, out var regularGlyph));
        Assert.Same(face, regularGlyph);

        Assert.True(wrapped.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Bold, FontStretch.Normal, null, CultureInfo.InvariantCulture, out var bold));
        Assert.True(wrapped.TryGetGlyphTypeface(bold.FontFamily.Name, bold.Style, bold.Weight, bold.Stretch, out var boldGlyph));
        Assert.NotNull(boldGlyph);
    }

    [AvaloniaFact]
    public void TryMatchCharacter_probes_alternate_weight_when_Normal_did_not_populate_inner_family_cache()
    {
        var face = LoadRealGlyphTypeface();
        var inner = new FakeSystemFontCollection();
        // Simulates BuildTypefaceWithSynthesis skipping TryAddGlyphTypeface for Normal,
        // then populating the inner cache once probed with Medium.
        inner.MatchResults[('✽', FontStyle.Normal, FontWeight.Normal)] =
            new Typeface(new FontFamily("fonts:SystemFonts#Noto Sans CJK SC"), FontStyle.Normal, FontWeight.Normal);
        inner.OnMatch = (cp, weight) =>
        {
            if (cp == '✽' && weight == FontWeight.Medium)
            {
                inner.SeedNearest("Noto Sans CJK SC", face);
                return new Typeface(new FontFamily("fonts:SystemFonts#Noto Sans CJK SC"), FontStyle.Normal, FontWeight.Medium);
            }
            return null;
        };

        var wrapped = new AndroidSystemFontCollection(inner);

        Assert.True(wrapped.TryMatchCharacter('✽', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, CultureInfo.InvariantCulture, out var matched));
        Assert.True(wrapped.TryGetGlyphTypeface(matched.FontFamily.Name, matched.Style, matched.Weight, matched.Stretch, out var resolved));
        Assert.Same(face, resolved);
    }

    [AvaloniaFact]
    public void TryMatchCharacter_returns_false_instead_of_poisoned_typeface_when_family_never_resolves()
    {
        var inner = new FakeSystemFontCollection();
        inner.MatchResults[('?', FontStyle.Normal, FontWeight.Normal)] =
            new Typeface(new FontFamily("fonts:SystemFonts#Broken Unnamed Font"), FontStyle.Normal, FontWeight.Normal);

        var wrapped = new AndroidSystemFontCollection(inner);

        Assert.False(wrapped.TryMatchCharacter('?', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, CultureInfo.InvariantCulture, out _));
    }

    [AvaloniaFact]
    public void Unknown_family_returns_false_without_matching_cached_fallback_families()
    {
        var face = LoadRealGlyphTypeface();
        var inner = new FakeSystemFontCollection();
        inner.SeedNearest("Noto Sans CJK SC", face);

        var wrapped = new AndroidSystemFontCollection(inner);

        Assert.False(wrapped.TryGetGlyphTypeface("Menlo", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
        Assert.False(wrapped.TryGetGlyphTypeface("Noto Sans", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
    }

    [AvaloniaFact]
    public void Non_Normal_character_match_primes_Normal_first_so_variable_fonts_keep_distinct_weights()
    {
        var normalFace = LoadRealGlyphTypeface(FontWeight.Normal);
        var mediumFace = LoadRealGlyphTypeface(FontWeight.Medium);
        var inner = new FakeSystemFontCollection();
        var seenWeights = new List<FontWeight>();
        inner.OnMatch = (cp, weight) =>
        {
            if (cp != '中') return null;
            seenWeights.Add(weight);
            if (weight == FontWeight.Normal)
            {
                inner.SeedExact("Noto Sans CJK SC", FontStyle.Normal, FontWeight.Normal, normalFace);
                return new Typeface(new FontFamily("fonts:SystemFonts#Noto Sans CJK SC"), FontStyle.Normal, FontWeight.Normal);
            }
            if (weight == FontWeight.Medium)
            {
                inner.SeedExact("Noto Sans CJK SC", FontStyle.Normal, FontWeight.Medium, mediumFace);
                return new Typeface(new FontFamily("fonts:SystemFonts#Noto Sans CJK SC"), FontStyle.Normal, FontWeight.Medium);
            }
            return null;
        };

        var wrapped = new AndroidSystemFontCollection(inner);

        Assert.True(wrapped.TryMatchCharacter('中', FontStyle.Normal, FontWeight.Medium, FontStretch.Normal, null, CultureInfo.InvariantCulture, out var mediumTypeface));
        Assert.Equal([FontWeight.Normal, FontWeight.Medium], seenWeights);
        Assert.True(wrapped.TryGetGlyphTypeface(mediumTypeface.FontFamily.Name, FontStyle.Normal, FontWeight.Medium, FontStretch.Normal, out var resolvedMedium));
        Assert.True(wrapped.TryGetGlyphTypeface(mediumTypeface.FontFamily.Name, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var resolvedNormal));
        Assert.Same(mediumFace, resolvedMedium);
        Assert.Same(normalFace, resolvedNormal);
    }

    private sealed class FakeSystemFontCollection : IFontCollection
    {
        private readonly Dictionary<string, GlyphTypeface> _nearest = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Family, FontStyle Style, FontWeight Weight), GlyphTypeface> _exact = new();

        public Dictionary<(int Codepoint, FontStyle Style, FontWeight Weight), Typeface> MatchResults { get; } = new();
        public Func<int, FontWeight, Typeface?>? OnMatch { get; set; }

        public void SeedNearest(string familyName, GlyphTypeface face) => _nearest[familyName] = face;

        public void SeedExact(string familyName, FontStyle style, FontWeight weight, GlyphTypeface face) =>
            _exact[(familyName, style, weight)] = face;

        public Uri Key { get; } = new("fonts:SystemFonts", UriKind.Absolute);
        public int Count => 0;
        public FontFamily this[int index] => throw new IndexOutOfRangeException();
        public IEnumerator<FontFamily> GetEnumerator() => Enumerable.Empty<FontFamily>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // Simulates SystemFontCollection.TryGetGlyphTypeface failing for unnamed Android fallback families.
        public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) =>
            _exact.TryGetValue((familyName, style, weight), out glyphTypeface);

        public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch, string? familyName, CultureInfo? culture, out Typeface typeface)
        {
            if (MatchResults.TryGetValue((codepoint, fontStyle, fontWeight), out typeface))
            {
                return true;
            }
            if (OnMatch?.Invoke(codepoint, fontWeight) is { } dynamicMatch)
            {
                typeface = dynamicMatch;
                return true;
            }
            typeface = default;
            return false;
        }

        public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
        {
            familyTypefaces = null;
            return false;
        }

        public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
        {
            syntheticGlyphTypeface = null;
            return false;
        }

        public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) =>
            _nearest.TryGetValue(familyName, out glyphTypeface);

        public void Dispose() { }
    }
}
