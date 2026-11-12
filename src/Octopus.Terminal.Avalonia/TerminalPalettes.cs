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

    /// <summary>Near-black, neutral background.</summary>
    public static TerminalPalette Dark { get; } = Build(Base16Dark, foreground: 0xD4D4D4, background: 0x161617, cursor: 0xD4D4D4);

    /// <summary>Near-white background.</summary>
    public static TerminalPalette Light { get; } = Build(Base16Light, foreground: 0x1F2328, background: 0xFAFAFA, cursor: 0x1F2328);

    /// <summary>The palette for a theme.</summary>
    public static TerminalPalette For(bool dark) => dark ? Dark : Light;

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
