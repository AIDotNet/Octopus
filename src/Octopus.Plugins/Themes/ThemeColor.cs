using System.Globalization;

namespace Octopus.Plugins.Themes;

/// <summary>A theme color: <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
public readonly record struct ThemeColor(byte R, byte G, byte B, byte A = 255)
{
    public static bool TryParse(string? text, out ThemeColor color)
    {
        color = default;
        if (text is null || text.Length is not (7 or 9) || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }
        color = text.Length == 7
            ? new ThemeColor((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new ThemeColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>Upper-case <c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when not opaque.</summary>
    public override string ToString() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    /// <summary>This color drawn over an opaque <paramref name="background"/>.</summary>
    public ThemeColor Over(ThemeColor background)
    {
        if (A == 255) return this;
        var alpha = A / 255.0;
        byte Mix(byte top, byte bottom) => (byte)Math.Round(top * alpha + bottom * (1 - alpha));
        return new ThemeColor(Mix(R, background.R), Mix(G, background.G), Mix(B, background.B));
    }

    /// <summary>WCAG 2 relative luminance of the color's RGB.</summary>
    public double Luminance
    {
        get
        {
            static double Channel(byte c)
            {
                var s = c / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
        }
    }

    /// <summary>WCAG 2 contrast ratio (1 to 21) of a foreground drawn over a background.</summary>
    public static double Contrast(ThemeColor foreground, ThemeColor background)
    {
        var bg = background.A == 255 ? background : background.Over(new ThemeColor(0, 0, 0));
        var a = foreground.Over(bg).Luminance;
        var b = bg.Luminance;
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
