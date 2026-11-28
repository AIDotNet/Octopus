using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>Light and dark 256-colour palettes. Entries 16-255 follow xterm's cube and grey ramp.</summary>
public static class TerminalPalettes
{
    private static readonly int[] Base16Dark =
    [
        0x1A1A1A, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
        0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF,
    ];

    // Yellows, whites and bright colours are darkened so they stay readable on a near-white background.
    private static readonly int[] Base16Light =
    [
        0x000000, 0xCD3131, 0x00A000, 0x949800, 0x0451A5, 0xBC05BC, 0x0598BC, 0x555555,
        0x666666, 0xCD3131, 0x14A814, 0xB5BA00, 0x0451A5, 0xBC05BC, 0x0598BC, 0xA5A5A5,
    ];

    private static TerminalPalette? s_customDark;
    private static TerminalPalette? s_customLight;

    /// <summary>Raised on the UI thread when plugin themes install or clear custom terminal palettes.</summary>
    public static event EventHandler? Changed;

    /// <summary>Near-black, neutral background.</summary>
    public static TerminalPalette Dark { get; } = Build(Base16Dark, foreground: 0xD4D4D4, background: 0x161617, cursor: 0xD4D4D4);

    /// <summary>Near-white background.</summary>
    public static TerminalPalette Light { get; } = Build(Base16Light, foreground: 0x1F2328, background: 0xFAFAFA, cursor: 0x1F2328);

    /// <summary>The palette for a theme (uses the active plugin theme's override when installed).</summary>
    public static TerminalPalette For(bool dark) => dark ? (s_customDark ?? Dark) : (s_customLight ?? Light);

    /// <summary>Installs or clears custom palettes provided by plugin themes.</summary>
    public static void SetCustomOverrides(TerminalPalette? light, TerminalPalette? dark)
    {
        s_customLight = light;
        s_customDark = dark;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Builds a 256-colour palette starting from the built-in dark or light base and overriding any provided fields.</summary>
    public static TerminalPalette BuildCustom(bool dark, int? foreground = null, int? background = null, int? cursor = null, IReadOnlyList<int>? ansi16 = null)
    {
        var basePalette = dark ? Dark : Light;
        var base16 = (int[])(dark ? Base16Dark : Base16Light).Clone();
        if (ansi16 is { Count: 16 })
        {
            for (var i = 0; i < 16; i++) base16[i] = ansi16[i];
        }
        var fg = foreground ?? Pack(basePalette.Foreground);
        var bg = background ?? Pack(basePalette.Background);
        var cur = cursor ?? Pack(basePalette.Cursor ?? basePalette.Foreground);
        return Build(base16, fg, bg, cur);
    }

    private static int Pack(TerminalRgb rgb) => (rgb.R << 16) | (rgb.G << 8) | rgb.B;

    /// <summary>The colour scheme the core reports to programs that ask.</summary>
    public static ColorScheme SchemeFor(bool dark) => dark ? ColorScheme.Dark : ColorScheme.Light;

    private static TerminalPalette Build(int[] base16, int foreground, int background, int cursor)
    {
        var colors = new TerminalRgb[256];
        for (var i = 0; i < 16; i++) colors[i] = Rgb(base16[i]);
        int[] levels = [0, 95, 135, 175, 215, 255];
        for (var i = 0; i < 216; i++)
            colors[16 + i] = Rgb((levels[i / 36] << 16) | (levels[i / 6 % 6] << 8) | levels[i % 6]);
        for (var i = 0; i < 24; i++)
        {
            var v = 8 + i * 10;
            colors[232 + i] = Rgb((v << 16) | (v << 8) | v);
        }
        return new TerminalPalette(Rgb(foreground), Rgb(background), Rgb(cursor), colors);
    }

    private static TerminalRgb Rgb(int v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v);
}
