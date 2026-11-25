using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Octopus.Mobile;

/// <summary>
/// Wraps Avalonia 12.2's system font collection under <c>fonts:SystemFonts</c> on Android so character-matched
/// unnamed fallback fonts (such as Android 15's variable <c>NotoSansCJK-Regular.ttc</c>, <c>Noto Sans Old Italic</c>,
/// and <c>Noto Serif Hentaigana ExtraLight</c>) always resolve back through <see cref="Typeface.GlyphTypeface"/>.
/// </summary>
/// <remarks>
/// Avalonia 12.2's internal <c>SystemFontCollection</c> breaks <see cref="FontManager.TryMatchCharacter"/> followed
/// by <see cref="Typeface.GlyphTypeface"/> (used by <c>TerminalView.FaceFor</c>) on Android in three ways:
/// <list type="number">
/// <item>Unnamed fallback fonts in <c>/system/etc/fonts.xml</c> are returned by <c>SKFontManager.MatchCharacter</c>
/// but return <c>null</c> from <c>SKFontManager.MatchFamily</c>. When a variable font like <c>NotoSansCJK-Regular.ttc</c>
/// is cached under a non-Normal weight (e.g. Medium 500) while its OS/2 table reports Normal (400), subsequent Normal
/// lookups for symbols or scripts in that font (such as <c>⎿</c> U+23BF, <c>✽</c> U+273D, <c>✓</c> U+2713) miss the
/// exact cache key, fail <c>MatchFamily</c>, and throw <see cref="InvalidOperationException"/>.</item>
/// <item><c>SystemFontCollection.TryGetGlyphTypeface</c> calls <see cref="Typeface.Normalize"/> before consulting the
/// cache, stripping style/weight words from real Android fallback family names like <c>"Noto Sans Old Italic"</c> and
/// <c>"Noto Serif Hentaigana ExtraLight"</c>.</item>
/// <item>A <c>MatchFamily</c> miss returns <c>false</c> instead of falling back to the nearest cached face of that
/// exact family via <see cref="IFontCollection.TryGetNearestMatch"/>.</item>
/// </list>
/// </remarks>
public sealed class AndroidSystemFontCollection(IFontCollection inner) : IFontCollection
{
    private readonly IFontCollection _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ConcurrentDictionary<LookupKey, GlyphTypeface> _resolved = new(LookupKeyComparer.Instance);

    public Uri Key { get; } = new("fonts:SystemFonts", UriKind.Absolute);

    public int Count => _inner.Count;

    public FontFamily this[int index] => _inner[index];

    public IEnumerator<FontFamily> GetEnumerator() => _inner.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool TryGetGlyphTypeface(
        string familyName,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        var key = new LookupKey(familyName, style, weight, stretch);
        if (_resolved.TryGetValue(key, out glyphTypeface))
        {
            return true;
        }

        if (_inner.TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface))
        {
            _resolved.TryAdd(key, glyphTypeface);
            return true;
        }

        // Fall back to the exact (un-normalized) family name already cached in the inner collection.
        // TryGetNearestMatch skips null negative-cache entries and does NOT strip style/weight keywords
        // or prefix-match unrelated families, so unknown families like "Menlo" still return false.
        if (TryResolveNearest(familyName, style, weight, stretch, out glyphTypeface))
        {
            _resolved.TryAdd(key, glyphTypeface);
            return true;
        }

        glyphTypeface = null;
        return false;
    }

    public bool TryMatchCharacter(
        int codepoint,
        FontStyle fontStyle,
        FontWeight fontWeight,
        FontStretch fontStretch,
        string? familyName,
        CultureInfo? culture,
        out Typeface typeface)
    {
        // Variable Android fallback fonts (e.g. NotoSansCJK-Regular.ttc) report their instanced weight on
        // IPlatformTypeface while their OS/2 table reports Normal. If first discovered at Medium/Bold (e.g. a
        // Navbar title), Avalonia's PreferPlatformMatch later mistakes that cached face for Normal. Priming Normal
        // first ensures Normal text gets its true Regular instance while Medium/Bold still resolve their own weights.
        if (fontWeight != FontWeight.Normal || fontStyle != FontStyle.Normal)
        {
            _ = _inner.TryMatchCharacter(codepoint, FontStyle.Normal, FontWeight.Normal, fontStretch, familyName, culture, out _);
        }

        if (!_inner.TryMatchCharacter(codepoint, fontStyle, fontWeight, fontStretch, familyName, culture, out typeface))
        {
            return false;
        }

        var matchedFamily = typeface.FontFamily.Name;
        if (TryGetGlyphTypeface(matchedFamily, typeface.Style, typeface.Weight, typeface.Stretch, out _))
        {
            return true;
        }

        // If inner's BuildTypefaceWithSynthesis skipped caching under matchedFamily because glyph.ToFontCollectionKey()
        // matched the requested key (e.g. an unnamed font whose OpenType FamilyName differs from its platform family
        // name), probing an alternate weight populates inner's cache under matchedFamily so nearest-match resolves it.
        foreach (var probeWeight in ProbeWeightsFor(fontWeight))
        {
            if (_inner.TryMatchCharacter(codepoint, fontStyle, probeWeight, fontStretch, matchedFamily, culture, out _)
                && TryGetGlyphTypeface(matchedFamily, typeface.Style, typeface.Weight, typeface.Stretch, out _))
            {
                return true;
            }
        }

        // Never hand back a Typeface whose GlyphTypeface property would throw InvalidOperationException on render.
        typeface = default;
        return false;
    }

    public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces) =>
        _inner.TryGetFamilyTypefaces(familyName, out familyTypefaces);

    public bool TryCreateSyntheticGlyphTypeface(
        GlyphTypeface glyphTypeface,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface) =>
        _inner.TryCreateSyntheticGlyphTypeface(glyphTypeface, style, weight, stretch, out syntheticGlyphTypeface);

    public bool TryGetNearestMatch(
        string familyName,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) =>
        _inner.TryGetNearestMatch(familyName, style, weight, stretch, out glyphTypeface);

    public void Dispose() => _inner.Dispose();

    private bool TryResolveNearest(
        string familyName,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        if (string.IsNullOrEmpty(familyName)
            || !_inner.TryGetNearestMatch(familyName, style, weight, stretch, out var nearest))
        {
            return false;
        }

        glyphTypeface = nearest;
        var needsSimulation = (style != FontStyle.Normal && nearest.Style == FontStyle.Normal)
            || (weight >= FontWeight.DemiBold && nearest.Weight < weight);
        if (needsSimulation && _inner.TryCreateSyntheticGlyphTypeface(nearest, style, weight, stretch, out var synthetic))
        {
            glyphTypeface = synthetic;
        }
        return true;
    }

    private static ReadOnlySpan<FontWeight> ProbeWeightsFor(FontWeight requested) =>
        requested == FontWeight.Normal
            ? [FontWeight.Medium, FontWeight.Bold]
            : [FontWeight.Normal, FontWeight.Bold];

    private readonly record struct LookupKey(string FamilyName, FontStyle Style, FontWeight Weight, FontStretch Stretch);

    private sealed class LookupKeyComparer : IEqualityComparer<LookupKey>
    {
        public static readonly LookupKeyComparer Instance = new();

        public bool Equals(LookupKey x, LookupKey y) =>
            x.Style == y.Style
            && x.Weight == y.Weight
            && x.Stretch == y.Stretch
            && string.Equals(x.FamilyName, y.FamilyName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(LookupKey obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FamilyName), obj.Style, obj.Weight, obj.Stretch);
    }
}
